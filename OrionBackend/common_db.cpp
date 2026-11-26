#include "pch.h"
#include "common_db.h"

#include <sqlite3.h>
#include <src/file_system.h>
#include <src/log.h>

#include <chrono>

namespace orion {

namespace {

constexpr const char* kAppId = "com.kagi.orion";
constexpr const char* kDefaultIcon = "<span foreground=\"#2646DE\">⬤</span>";
constexpr const char* kEngineSettingKey = "settings::general::browser_engine";

// The dummy provisioning row must not collide with the unique seq_num index.
constexpr double kPreseedSeqNum = -100000.0;

bool execSql(sqlite3* db, const std::string& sql)
{
    char* err = nullptr;
    if (sqlite3_exec(db, sql.c_str(), nullptr, nullptr, &err) != SQLITE_OK) {
        orion::log_error("CommonDb> exec failed: %s (%s)", sql.c_str(), err ? err : "?");
        sqlite3_free(err);
        return false;
    }
    return true;
}

// Minimal RAII prepared statement.
class Stmt {
public:
    Stmt(sqlite3* db, const char* sql)
    {
        if (sqlite3_prepare_v2(db, sql, -1, &m_stmt, nullptr) != SQLITE_OK) {
            orion::log_error("CommonDb> prepare failed: %s (%s)", sql, sqlite3_errmsg(db));
            m_stmt = nullptr;
        }
    }
    ~Stmt() { sqlite3_finalize(m_stmt); }
    Stmt(const Stmt&) = delete;
    Stmt& operator=(const Stmt&) = delete;

    bool valid() const { return m_stmt != nullptr; }
    Stmt& bind(int index, int64_t value) { sqlite3_bind_int64(m_stmt, index, value); return *this; }
    Stmt& bind(int index, double value) { sqlite3_bind_double(m_stmt, index, value); return *this; }
    Stmt& bind(int index, const std::string& value)
    {
        sqlite3_bind_text(m_stmt, index, value.c_str(), (int)value.size(), SQLITE_TRANSIENT);
        return *this;
    }
    bool step_row() { return m_stmt && sqlite3_step(m_stmt) == SQLITE_ROW; }
    bool step_done() { return m_stmt && sqlite3_step(m_stmt) == SQLITE_DONE; }

    int64_t column_int64(int col) { return sqlite3_column_int64(m_stmt, col); }
    double column_double(int col) { return sqlite3_column_double(m_stmt, col); }
    std::string column_text(int col)
    {
        const unsigned char* text = sqlite3_column_text(m_stmt, col);
        return text ? reinterpret_cast<const char*>(text) : "";
    }

private:
    sqlite3_stmt* m_stmt = nullptr;
};

bool tableExists(sqlite3* db, const std::string& table)
{
    Stmt stmt(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?1");
    return stmt.valid() && (stmt.bind(1, table), stmt.step_row());
}

sqlite3* openSqlite(const std::filesystem::path& path)
{
    sqlite3* db = nullptr;
    if (sqlite3_open_v2(path.string().c_str(), &db,
            SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nullptr) != SQLITE_OK) {
        orion::log_error("CommonDb> cannot open %s: %s",
            path.string().c_str(), db ? sqlite3_errmsg(db) : "?");
        sqlite3_close(db);
        return nullptr;
    }
    // foreign_keys is per-connection and defaults to OFF; the legacy split
    // depends on ON DELETE CASCADE, so set it on every connection we open.
    execSql(db, "PRAGMA foreign_keys = ON;"
                "PRAGMA journal_mode = WAL;"
                "PRAGMA synchronous = FULL;");
    return db;
}

std::string profileEngineFromLegacy(sqlite3* legacy, int64_t profile_id)
{
    Stmt stmt(legacy, "SELECT value FROM settings WHERE profile_id = ?1 AND key = ?2");
    if (stmt.valid() && (stmt.bind(1, profile_id).bind(2, std::string(kEngineSettingKey)), stmt.step_row())) {
        std::string engine = stmt.column_text(0);
        if (engine == "blink")
            return "blink";
    }
    return "webkit";
}

// Tables carrying a profile_id column (each DELETE cascades through its own
// subtree: windows -> tabs -> back-forward lists, extensions -> storage
// areas, ...). Used to strip the internal profile's data from non-default
// copies. Guarded by tableExists so older legacy schemas split cleanly.
constexpr const char* kProfileScopedTables[] = {
    "reading_list_entries", "windows", "bookmarks", "bookmark_folders",
    "history_entries", "settings", "extensions", "snapshot_tabs",
    "recently_closed_tabs", "recently_closed_windows", "password_items",
    "downloads", "search_engines", "filter_lists", "overflow_menu_items",
    "programmable_buttons", "toolbar_items",
};

} // namespace

CommonDb& CommonDb::instance()
{
    static CommonDb db;
    return db;
}

std::filesystem::path CommonDb::profiles_root()
{
    return std::filesystem::path(orion::data_directory_path(kAppId, { "Profiles" }));
}

std::filesystem::path CommonDb::profile_dir(int64_t id)
{
    return profiles_root() / std::to_string(id);
}

std::filesystem::path CommonDb::profile_db_path(int64_t id)
{
    return profile_dir(id) / "profile.db";
}

bool CommonDb::open()
{
    if (m_db)
        return true;

    const std::filesystem::path path = m_in_memory ? ":memory:"
        : orion::data_directory_path(kAppId, { "orion.db" });
    if (!m_in_memory) {
        std::error_code ec;
        std::filesystem::create_directories(path.parent_path(), ec);
    }

    m_db = openSqlite(path);
    if (!m_db)
        return false;
    if (!apply_schema()) {
        sqlite3_close(m_db);
        m_db = nullptr;
        return false;
    }
    return true;
}

bool CommonDb::use_in_memory_storage()
{
    if (m_db)
        return m_in_memory;
    m_in_memory = true;
    return true;
}

bool CommonDb::apply_schema()
{
    if (!execSql(m_db,
        "CREATE TABLE IF NOT EXISTS meta ("
        "  key TEXT PRIMARY KEY,"
        "  value TEXT NOT NULL DEFAULT ''"
        ");"
        "CREATE TABLE IF NOT EXISTS profiles ("
        "  id INTEGER PRIMARY KEY,"
        "  name TEXT NOT NULL DEFAULT '',"
        "  icon TEXT NOT NULL DEFAULT '',"
        "  engine TEXT NOT NULL DEFAULT 'webkit',"
        "  seq_num REAL NOT NULL DEFAULT 0,"
        "  is_default INTEGER NOT NULL DEFAULT 0,"
        "  show_in_address_bar INTEGER NOT NULL DEFAULT 1,"
        "  show_badge_in_app_icon INTEGER NOT NULL DEFAULT 1,"
        "  keyboard_shortcut TEXT NOT NULL DEFAULT '',"
        "  deleted_at INTEGER NOT NULL DEFAULT 0"
        ");"
        "CREATE TABLE IF NOT EXISTS app_settings ("
        "  key TEXT PRIMARY KEY,"
        "  value TEXT NOT NULL DEFAULT '',"
        "  type TEXT NOT NULL DEFAULT 'text'"
        ");"
        "INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '2');"))
        return false;

    // v1 -> v2: deleted profiles keep their row (deleted_at != 0). The id
    // stays occupied forever — create_profile's MAX(id)+1 sees the row — so
    // the deterministic per-id data dirs can never be reattached to a new
    // profile, and the row doubles as the durable record that the profile's
    // data dirs are owed deletion (ProfileDataCleanup).
    bool needs_v2 = false;
    {
        Stmt version(m_db, "SELECT value FROM meta WHERE key = 'schema_version'");
        needs_v2 = version.valid() && version.step_row()
            && std::string(version.column_text(0)) == "1";
    }
    if (needs_v2) {
        if (!execSql(m_db,
            "ALTER TABLE profiles ADD COLUMN deleted_at INTEGER NOT NULL DEFAULT 0;"
            "UPDATE meta SET value = '2' WHERE key = 'schema_version';"))
            return false;
    }
    return true;
}

std::vector<RegistryProfile> CommonDb::get_profiles()
{
    std::vector<RegistryProfile> result;
    if (!open())
        return result;
    Stmt stmt(m_db,
        "SELECT id, name, icon, engine, seq_num, is_default, show_in_address_bar,"
        "       show_badge_in_app_icon, keyboard_shortcut"
        " FROM profiles WHERE deleted_at = 0 ORDER BY seq_num");
    while (stmt.step_row()) {
        RegistryProfile p;
        p.id = stmt.column_int64(0);
        p.name = stmt.column_text(1);
        p.icon = stmt.column_text(2);
        p.engine = stmt.column_text(3);
        p.seq_num = stmt.column_double(4);
        p.is_default = stmt.column_int64(5) != 0;
        p.show_in_address_bar = stmt.column_int64(6) != 0;
        p.show_badge_in_app_icon = stmt.column_int64(7) != 0;
        p.keyboard_shortcut = stmt.column_text(8);
        result.push_back(std::move(p));
    }
    return result;
}

std::optional<RegistryProfile> CommonDb::get_profile(int64_t id)
{
    for (RegistryProfile& p : get_profiles()) {
        if (p.id == id)
            return p;
    }
    return std::nullopt;
}

std::optional<RegistryProfile> CommonDb::get_default_profile()
{
    std::vector<RegistryProfile> profiles = get_profiles();
    for (RegistryProfile& p : profiles) {
        if (p.is_default)
            return p;
    }
    // Self-heal a missing default flag.
    if (!profiles.empty()) {
        set_default_profile(profiles.front().id);
        profiles.front().is_default = true;
        return profiles.front();
    }
    return std::nullopt;
}

int64_t CommonDb::create_profile(
    const std::string& name, const std::string& icon, const std::string& engine)
{
    if (!open())
        return 0;

    int64_t id = 0;
    {
        Stmt stmt(m_db,
            "INSERT INTO profiles (id, name, icon, engine, seq_num, is_default)"
            " VALUES ("
            "   MAX(?4, COALESCE((SELECT MAX(id) + 1 FROM profiles), ?4)),"
            "   ?1, ?2, ?3,"
            "   COALESCE((SELECT MAX(seq_num) + 1 FROM profiles), 1),"
            "   (SELECT COUNT(*) FROM profiles) = 0"
            " ) RETURNING id");
        if (!stmt.valid())
            return 0;
        stmt.bind(1, name).bind(2, icon.empty() ? std::string(kDefaultIcon) : icon)
            .bind(3, engine.empty() ? std::string("webkit") : engine)
            .bind(4, kMinProfileId);
        if (!stmt.step_row())
            return 0;
        id = stmt.column_int64(0);
    }

    if (!m_in_memory) {
        std::error_code ec;
        std::filesystem::create_directories(profile_dir(id), ec);
        if (ec)
            orion::log_error("CommonDb> cannot create profile dir for %lld: %s",
                (long long)id, ec.message().c_str());
    }
    return id;
}

bool CommonDb::update_profile(const RegistryProfile& profile)
{
    if (!open())
        return false;
    Stmt stmt(m_db,
        "UPDATE profiles SET name = ?2, icon = ?3, engine = ?4,"
        " show_in_address_bar = ?5, show_badge_in_app_icon = ?6,"
        " keyboard_shortcut = ?7 WHERE id = ?1");
    return stmt.valid()
        && (stmt.bind(1, profile.id).bind(2, profile.name).bind(3, profile.icon)
                .bind(4, profile.engine)
                .bind(5, (int64_t)(profile.show_in_address_bar ? 1 : 0))
                .bind(6, (int64_t)(profile.show_badge_in_app_icon ? 1 : 0))
                .bind(7, profile.keyboard_shortcut),
            stmt.step_done());
}

bool CommonDb::set_default_profile(int64_t id)
{
    if (!open())
        return false;
    return execSql(m_db, "BEGIN IMMEDIATE;"
                         "UPDATE profiles SET is_default = 0 WHERE id != " + std::to_string(id) + ";"
                         "UPDATE profiles SET is_default = 1 WHERE id = " + std::to_string(id) + ";"
                         "COMMIT;");
}

bool CommonDb::move_profile(int64_t id, int64_t next_id)
{
    if (!open())
        return false;

    double new_seq = 0;
    if (next_id == 0) {
        Stmt stmt(m_db, "SELECT COALESCE(MAX(seq_num) + 1, 1) FROM profiles");
        if (!stmt.step_row())
            return false;
        new_seq = stmt.column_double(0);
    } else {
        Stmt next(m_db, "SELECT seq_num FROM profiles WHERE id = ?1");
        if (!(next.valid() && (next.bind(1, next_id), next.step_row())))
            return false;
        double next_seq = next.column_double(0);
        Stmt prev(m_db, "SELECT COALESCE(MAX(seq_num), ?1 - 2) FROM profiles"
                        " WHERE seq_num < ?1 AND id != ?2");
        if (!(prev.valid() && (prev.bind(1, next_seq).bind(2, id), prev.step_row())))
            return false;
        new_seq = (prev.column_double(0) + next_seq) / 2;
    }

    Stmt stmt(m_db, "UPDATE profiles SET seq_num = ?2 WHERE id = ?1");
    return stmt.valid() && (stmt.bind(1, id).bind(2, new_seq), stmt.step_done());
}

bool CommonDb::delete_profile(int64_t id)
{
    if (!open())
        return false;

    std::vector<RegistryProfile> profiles = get_profiles();
    if (profiles.size() <= 1) {
        orion::log_warning("CommonDb> refusing to delete the last profile");
        return false;
    }

    bool was_default = false;
    for (const RegistryProfile& p : profiles)
        if (p.id == id)
            was_default = p.is_default;

    {
        // Keep the row with deleted_at set: the id stays occupied forever
        // (create_profile allocates MAX(id)+1 over all rows), so the
        // deterministic per-id data dirs can never be reattached to a new
        // profile, and the row is the durable record that this profile's
        // data dirs are owed deletion (processed by ProfileDataCleanup once
        // the owning process is fully gone).
        auto stamp = std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count();
        Stmt stmt(m_db, "UPDATE profiles SET deleted_at = ?2, is_default = 0 WHERE id = ?1");
        if (!(stmt.valid() && (stmt.bind(1, id).bind(2, (int64_t)stamp), stmt.step_done())))
            return false;
    }
    if (was_default) {
        for (const RegistryProfile& p : profiles) {
            if (p.id != id) {
                set_default_profile(p.id);
                break;
            }
        }
    }

    // Registry mutation only — the data dirs are swept by ProfileDataCleanup
    // in a surviving process (an inline delete could eat a still-running
    // target's files, and a process cannot remove its own dirs anyway).
    return true;
}

std::vector<int64_t> CommonDb::deleted_profile_ids()
{
    std::vector<int64_t> result;
    if (!open())
        return result;
    Stmt stmt(m_db, "SELECT id FROM profiles WHERE deleted_at != 0");
    while (stmt.step_row())
        result.push_back(stmt.column_int64(0));
    return result;
}

std::optional<std::string> CommonDb::get_app_setting(const std::string& key)
{
    if (!open())
        return std::nullopt;
    Stmt stmt(m_db, "SELECT value FROM app_settings WHERE key = ?1");
    if (stmt.valid() && (stmt.bind(1, key), stmt.step_row()))
        return stmt.column_text(0);
    return std::nullopt;
}

bool CommonDb::set_app_setting(
    const std::string& key, const std::string& value, const std::string& type)
{
    if (!open())
        return false;
    Stmt stmt(m_db,
        "INSERT INTO app_settings (key, value, type) VALUES (?1, ?2, ?3)"
        " ON CONFLICT(key) DO UPDATE SET value = excluded.value, type = excluded.type");
    return stmt.valid() && (stmt.bind(1, key).bind(2, value).bind(3, type), stmt.step_done());
}

// ---------------------------------------------------------------------------
// Legacy split: one profiles.db -> Profiles\<id>\profile.db per profile.
// ---------------------------------------------------------------------------

bool CommonDb::migrate_legacy_if_needed()
{
    if (!open())
        return false;
    if (m_in_memory)
        return true;

    {
        Stmt stmt(m_db, "SELECT COUNT(*) FROM profiles");
        if (stmt.step_row() && stmt.column_int64(0) > 0)
            return true;  // registry populated — split already done (or fresh+created)
    }

    std::filesystem::path legacy_path(orion::data_directory_path(kAppId, { "profiles.db" }));
    if (!std::filesystem::exists(legacy_path))
        return true;  // fresh install; boot creates the default profile

    orion::log_info("CommonDb> splitting legacy profiles.db into per-profile databases");

    struct LegacyProfile {
        int64_t id;
        std::string name, icon, shortcut, engine;
        double seq_num;
        bool is_default, show_in_address_bar, show_badge_in_app_icon;
    };
    std::vector<LegacyProfile> legacy_profiles;
    int64_t internal_id = 1;
    int64_t default_id = 0;

    {
        sqlite3* legacy = openSqlite(legacy_path);
        if (!legacy)
            return false;
        execSql(legacy, "PRAGMA wal_checkpoint(TRUNCATE);");

        Stmt internal(legacy, "SELECT id FROM profiles WHERE is_internal = 1 LIMIT 1");
        if (internal.step_row())
            internal_id = internal.column_int64(0);

        // Older schemas may lack the is_test column — probe before filtering.
        bool has_is_test = false;
        {
            Stmt probe(legacy,
                "SELECT COUNT(*) FROM pragma_table_info('profiles') WHERE name = 'is_test'");
            if (probe.step_row())
                has_is_test = probe.column_int64(0) > 0;
        }
        std::string select_sql =
            "SELECT id, name, icon_name, keyboard_shortcut, seq_num, is_default,"
            "       show_in_address_bar, show_badge_in_app_icon"
            " FROM profiles WHERE is_internal = 0";
        if (has_is_test)
            select_sql += " AND is_test = 0";
        select_sql += " ORDER BY seq_num";
        Stmt stmt(legacy, select_sql.c_str());
        if (!stmt.valid())
            return false;
        while (stmt.step_row()) {
            LegacyProfile p;
            p.id = stmt.column_int64(0);
            p.name = stmt.column_text(1);
            p.icon = stmt.column_text(2);
            p.shortcut = stmt.column_text(3);
            p.seq_num = stmt.column_double(4);
            p.is_default = stmt.column_int64(5) != 0;
            p.show_in_address_bar = stmt.column_int64(6) != 0;
            p.show_badge_in_app_icon = stmt.column_int64(7) != 0;
            p.engine = profileEngineFromLegacy(legacy, p.id);
            if (p.is_default)
                default_id = p.id;
            legacy_profiles.push_back(std::move(p));
        }

        // App-wide settings: everything in common_settings moves to orion.db.
        if (tableExists(legacy, "common_settings")) {
            Stmt cs(legacy, "SELECT key, value, type FROM common_settings");
            while (cs.step_row())
                set_app_setting(cs.column_text(0), cs.column_text(1), cs.column_text(2));
        }

        sqlite3_close(legacy);
    }

    if (legacy_profiles.empty()) {
        orion::log_warning("CommonDb> legacy DB has no real profiles — treating as fresh");
        return true;
    }
    if (default_id == 0)
        default_id = legacy_profiles.front().id;

    // Split: copy the whole file per profile, then carve away everything that
    // is not that profile's. The internal profile's own data (pre-profile-era
    // history/bookmarks) stays only in the default profile's copy.
    for (const LegacyProfile& p : legacy_profiles) {
        std::filesystem::path target = profile_db_path(p.id);
        std::error_code ec;
        std::filesystem::create_directories(target.parent_path(), ec);
        std::filesystem::copy_file(
            legacy_path, target, std::filesystem::copy_options::overwrite_existing, ec);
        if (ec) {
            orion::log_error("CommonDb> copy to %s failed: %s",
                target.string().c_str(), ec.message().c_str());
            return false;
        }

        sqlite3* db = openSqlite(target);
        if (!db)
            return false;

        bool ok = execSql(db, "BEGIN IMMEDIATE;");
        {
            Stmt del(db, "DELETE FROM profiles WHERE id NOT IN (?1, ?2)");
            ok = ok && del.valid() && (del.bind(1, internal_id).bind(2, p.id), del.step_done());
        }
        // Rows orphaned before the split (children of profiles deleted while
        // FK enforcement was off) are unreachable by the CASCADE above and
        // would fail the foreign_key_check gate below — purge them.
        for (const char* table : kProfileScopedTables) {
            if (!tableExists(db, table))
                continue;
            ok = ok && execSql(db, "DELETE FROM " + std::string(table)
                + " WHERE profile_id NOT IN (SELECT id FROM profiles);");
        }
        // Deeper strays (e.g. tabs of a window that was itself already
        // orphaned): delete exactly the rows foreign_key_check reports; a
        // deletion can expose the next level, so iterate until clean.
        for (int pass = 0; ok && pass < 8; ++pass) {
            std::vector<std::pair<std::string, int64_t>> dangling;
            {
                Stmt check(db, "PRAGMA foreign_key_check");
                while (check.step_row())
                    dangling.emplace_back(check.column_text(0), check.column_int64(1));
            }
            if (dangling.empty())
                break;
            orion::log_info("CommonDb> purging %zu dangling row(s) left from"
                " pre-split deletions", dangling.size());
            for (const auto& [table, rowid] : dangling) {
                Stmt del(db, ("DELETE FROM " + table + " WHERE rowid = ?1").c_str());
                ok = ok && del.valid() && (del.bind(1, rowid), del.step_done());
            }
        }
        if (p.id != default_id) {
            // Strip the internal profile's child data (kept only in the
            // default profile's copy). The profiles row itself must remain.
            for (const char* table : kProfileScopedTables) {
                if (!tableExists(db, table))
                    continue;
                Stmt del(db, ("DELETE FROM " + std::string(table)
                    + " WHERE profile_id = ?1").c_str());
                ok = ok && del.valid() && (del.bind(1, internal_id), del.step_done());
            }
            ok = ok && execSql(db,
                "UPDATE profiles SET shared_pinned_tabs_window_id = NULL,"
                " selected_window_id = 0 WHERE id = " + std::to_string(internal_id) + ";");
        }
        // common_settings lives in orion.db now; leftover copies would diverge.
        if (tableExists(db, "common_settings"))
            ok = ok && execSql(db, "DELETE FROM common_settings;");
        // In-DB default flag: this profile. (The registry stays the authority;
        // this keeps core-internal "default profile" logic pointing at the
        // only real profile in the file.)
        ok = ok && execSql(db,
            "UPDATE profiles SET is_default = 0 WHERE id != " + std::to_string(p.id) + ";"
            "UPDATE profiles SET is_default = 1 WHERE id = " + std::to_string(p.id) + ";");
        if (ok)
            ok = execSql(db, "COMMIT;");
        else
            execSql(db, "ROLLBACK;");

        if (ok) {
            Stmt check(db, "PRAGMA foreign_key_check");
            if (check.step_row()) {
                orion::log_error("CommonDb> foreign_key_check failed for profile %lld",
                    (long long)p.id);
                ok = false;
            }
        }
        if (ok)
            execSql(db, "VACUUM;");
        sqlite3_close(db);
        if (!ok)
            return false;
    }

    // Seed the registry (ids preserved from the legacy DB).
    for (const LegacyProfile& p : legacy_profiles) {
        Stmt stmt(m_db,
            "INSERT INTO profiles (id, name, icon, engine, seq_num, is_default,"
            " show_in_address_bar, show_badge_in_app_icon, keyboard_shortcut)"
            " VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)");
        if (!(stmt.valid()
                && (stmt.bind(1, p.id).bind(2, p.name).bind(3, p.icon).bind(4, p.engine)
                        .bind(5, p.seq_num).bind(6, (int64_t)(p.id == default_id ? 1 : 0))
                        .bind(7, (int64_t)(p.show_in_address_bar ? 1 : 0))
                        .bind(8, (int64_t)(p.show_badge_in_app_icon ? 1 : 0))
                        .bind(9, p.shortcut),
                    stmt.step_done())))
            return false;
    }

    // Retire the legacy file (WAL/SHM were checkpointed away above).
    std::error_code ec;
    std::filesystem::rename(legacy_path, legacy_path.string() + ".pre-split.bak", ec);
    std::filesystem::remove(legacy_path.string() + "-wal", ec);
    std::filesystem::remove(legacy_path.string() + "-shm", ec);

    orion::log_info("CommonDb> legacy split complete: %zu profile(s)", legacy_profiles.size());
    return true;
}

// ---------------------------------------------------------------------------
// New-profile provisioning helpers.
// ---------------------------------------------------------------------------

bool CommonDb::preseed_profile_rowid(
    const std::filesystem::path& profile_db, int64_t target_id)
{
    sqlite3* db = openSqlite(profile_db);
    if (!db)
        return false;

    bool ok = false;
    {
        Stmt max_stmt(db, "SELECT COALESCE(MAX(id), 0) FROM profiles");
        if (max_stmt.step_row()) {
            int64_t max_id = max_stmt.column_int64(0);
            if (max_id == target_id - 1) {
                ok = true;  // rowid assignment already lands on target_id
            } else if (max_id < target_id - 1) {
                Stmt ins(db,
                    "INSERT INTO profiles (id, name, icon_name, seq_num, is_test)"
                    " VALUES (?1, '', '', ?2, 1)");
                ok = ins.valid()
                    && (ins.bind(1, target_id - 1).bind(2, kPreseedSeqNum), ins.step_done());
            } else {
                orion::log_error(
                    "CommonDb> preseed: profile DB already contains id %lld >= target %lld",
                    (long long)max_id, (long long)target_id);
            }
        }
    }
    sqlite3_close(db);
    return ok;
}

void CommonDb::remove_preseed_row(
    const std::filesystem::path& profile_db, int64_t target_id)
{
    sqlite3* db = openSqlite(profile_db);
    if (!db)
        return;
    Stmt del(db, "DELETE FROM profiles WHERE id = ?1 AND is_test = 1");
    if (del.valid()) {
        del.bind(1, target_id - 1);
        del.step_done();
    }
    sqlite3_close(db);
}

} // namespace orion
