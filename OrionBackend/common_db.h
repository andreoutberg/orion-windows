#pragma once

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

struct sqlite3;

namespace orion {

// A row of the cross-profile registry in orion.db. Only real (user-visible)
// profiles live here; core's hidden internal profile exists solely inside
// each per-profile database.
struct RegistryProfile {
    int64_t id = 0;
    std::string name;
    std::string icon;
    std::string engine = "webkit";  // "webkit" | "blink"
    double seq_num = 0.0;
    bool is_default = false;
    bool show_in_address_bar = true;
    bool show_badge_in_app_icon = true;
    std::string keyboard_shortcut;
};

// The common (cross-profile) database: %LOCALAPPDATA%\com.kagi.orion\data\
// orion.db. Holds the profile registry (which profiles exist, their names,
// icons, ENGINES, order, the default flag) and app-wide settings shared by
// every profile. Owned by the facade, UI-thread only, synchronous sqlite —
// the data is tiny. This is the source of truth for profile enumeration and
// the default profile; the per-profile core databases' own profiles tables
// are implementation detail (their is_default flags diverge after the
// legacy split and must not be consulted).
class CommonDb {
public:
    static CommonDb& instance();

    // Opens (creating if needed) orion.db and applies the schema. Idempotent;
    // returns false on failure (subsequent calls retry).
    bool open();
    bool is_open() const { return m_db != nullptr; }
    bool use_in_memory_storage();
    bool is_in_memory() const { return m_in_memory; }

    // One-time split of a legacy single profiles.db into per-profile
    // databases + registry seeding. No-op when orion.db already has profiles
    // or no legacy file exists. Must run before core opens any profile DB.
    // Returns false only on a real failure (legacy present but split failed).
    bool migrate_legacy_if_needed();

    // --- profile registry -------------------------------------------------
    std::vector<RegistryProfile> get_profiles();  // ordered by seq_num
    std::optional<RegistryProfile> get_profile(int64_t id);
    std::optional<RegistryProfile> get_default_profile();
    // Creates a registry row (id = max+1, never <= kMinProfileId-1) and the
    // profile's data directory. Returns 0 on failure.
    int64_t create_profile(const std::string& name, const std::string& icon,
        const std::string& engine);
    bool update_profile(const RegistryProfile& profile);
    bool set_default_profile(int64_t id);
    // Places `id` before `next_id` in seq_num order (next_id 0 = move to end).
    bool move_profile(int64_t id, int64_t next_id);
    // Marks the profile deleted. The row is kept (deleted_at != 0) so the id
    // is never reused, and it records that the data dirs are owed deletion;
    // the inline removal attempt succeeds only when the profile is not
    // running (ProfileDataCleanup handles the rest). Refuses to delete the
    // last profile; reassigns the default when the default is deleted.
    bool delete_profile(int64_t id);
    // Ids of deleted profile rows — data-cleanup candidates.
    std::vector<int64_t> deleted_profile_ids();

    // --- app-wide settings ------------------------------------------------
    std::optional<std::string> get_app_setting(const std::string& key);
    bool set_app_setting(const std::string& key, const std::string& value,
        const std::string& type);

    // --- paths --------------------------------------------------------------
    static std::filesystem::path profiles_root();          // <data>\Profiles
    static std::filesystem::path profile_dir(int64_t id);  // <data>\Profiles\<id>
    static std::filesystem::path profile_db_path(int64_t id);

    // --- new-profile DB provisioning ---------------------------------------
    // Before core's CreateProfile runs against a freshly migrated profile.db,
    // steer SQLite's rowid assignment (max+1) so the created profile receives
    // exactly `target_id`: inserts a throwaway profiles row with id
    // target_id-1 when needed. Returns false when the DB already contains an
    // id >= target_id (unexpected content).
    static bool preseed_profile_rowid(const std::filesystem::path& profile_db,
        int64_t target_id);
    // Removes the throwaway row (id = target_id-1, is_test = 1) if present.
    static void remove_preseed_row(const std::filesystem::path& profile_db,
        int64_t target_id);

    // Registry ids never collide with core's internal profile (id 1 in every
    // per-profile DB); the first real profile id is 2.
    static constexpr int64_t kMinProfileId = 2;

private:
    CommonDb() = default;
    bool apply_schema();

    sqlite3* m_db = nullptr;
    bool m_in_memory = false;
};

} // namespace orion
