#include "pch.h"
#include "common_db.h"
#include "notification_bridge.h"
#include "bridge_hosts.h"
#include "script_bridge.h"
#include "generated/native_types.h"
#include "generated/orion_version.h"
#include "core/core_api.h"
#include "core/dto/history_entry_dto.h"
#include "core/dto/favicon_dto.h"
#include "core/dto/bookmark_dto.h"
#include "core/dto/bookmark_folder_dto.h"
#include "core/dto/profile_dto.h"
#include "core/dto/reading_list_entry_dto.h"
#include "core/dto/download_dto.h"
#include "core/dto/extension_dto.h"
#include "core/dto/window_dto.h"
#include "core/dto/tab_dto.h"
#include "core/dto/recently_closed_tab_dto.h"
#include "core/dto/recently_closed_window_dto.h"
#include "core/dto/undo_redo_tab_dto.h"
#include "core/dto/snapshot_dto.h"
#include "core/sync/sync_service.h"
#include "core/webextensions/extension.h"
#include "core/webextensions/internal_store.h"
#include "core/webextensions/install_source.h"
#include "core/webextensions/web_view.h"
#include "common/src/run_loop.h"
#include "common/src/log.h"
#include <cassert>
#include <chrono>
#include <map>
#include <memory>
#include <cstdlib>
#include <string>
#include <cstdio>
#include <io.h>
#include <fcntl.h>
#include <thread>

#include "utils.h"
#include "browser_engine.h"
#include "engine_loader.h"
#include "host_api.h"
#include "download_bridge.h"
#include "dpapi_password_storage.h"

#include <filesystem>
#include <shlobj.h>

// Windows headers #define CreateWindow → CreateWindowExW; undo so we can
// use the name in our own exports without macro expansion.
#undef CreateWindow

#ifdef ORION_BLINK_ONLY
static constexpr char DefaultBrowserEngine[] = "blink";
#else
static constexpr char DefaultBrowserEngine[] = "webkit";
#endif

struct InstalledExtensionNative {
    char* id;          // UUID string
    char* name;
    char* version;
    int32_t type;      // ExtensionType: 1=firefox, 2=chrome
    int8_t is_enabled;
};

struct ExtensionToolbarItemNative {
    char* id;          // extension UUID
    char* title;       // button tooltip (from default_title or name)
    char* popup_uri;   // moz-extension:// or chrome-extension:// URI
    uint8_t* icon_data;
    int32_t icon_data_len;
};

struct ProfileNative {
    int64_t id;
    char* name;
    char* icon_name;
    int8_t show_in_address_bar;
    int8_t show_badge_in_app_icon;
    char* keyboard_shortcut;
    int8_t is_internal;
    int8_t is_default;
    char* engine;  // "webkit" | "blink" (from the registry)
};

struct WindowNative {
    int64_t id;
    int64_t profile_id;
    char*   name;
    int8_t  is_tab_group;
    int64_t selected_tab_id;
    int8_t  has_ui_window;
    int8_t  is_private;
    char*   keyboard_shortcut;  // GTK accelerator name; "" = unbound
};

struct TabNative {
    int64_t id;
    int64_t window_id;
    char*   name;
    char*   uri;
    int8_t  is_pinned;
    int64_t parent_tab_id;
    int8_t  is_collapsed;
};

struct RecentlyClosedTabNative {
    int64_t id;
    int64_t profile_id;
    char*   name;
    char*   uri;
    int64_t closed_at;
    int64_t window_id; // 0 if not part of a closed window
};

struct RecentlyClosedWindowNative {
    int64_t id;
    int64_t profile_id;
    char*   name;
    int64_t closed_at;
};

struct DownloadEntryNative {
    int64_t id;
    int64_t profile_id;
    char*   name;
    char*   path;
    int32_t operation_state;
    int64_t timestamp;
    int64_t total_size;
    int64_t current_size;
    char*   origin_uri;
};

// Download event callbacks set by C# via SetDownloadCallbacks.
typedef void(__cdecl* DownloadChangedCallback)(const DownloadEntryNative* entry);
typedef void(__cdecl* DownloadDeletedCallback)(int64_t download_id);
static DownloadChangedCallback g_download_created_cb = nullptr;
static DownloadChangedCallback g_download_updated_cb = nullptr;
static DownloadDeletedCallback g_download_deleted_cb = nullptr;
static orion::ObserverId g_downloads_observer{};

static std::shared_ptr<orion::CoreApi> g_core_api;
static orion::DpapiPasswordStorage g_dpapi_password_storage;
static orion::HostNotificationProvider g_host_notification_provider;
static HANDLE g_wakeup_event = NULL;

// The loaded engine's half of the WebExtensions host contract (script_bridge.h);
// null when the engine has none (CEF), which disables extensions.
static orion::ScriptBridge* g_script_bridge = nullptr;

// Core host attachments (bridge_hosts.h). One profile per process and no
// private mode yet, so each profile activates exactly one world.
struct ProfileExtensionHosts {
    std::shared_ptr<orion::win::ProfileHost> profile;
    std::unique_ptr<orion::win::WebViewFactoryHost> factory;
    // Set by the factory bridge's when_ready() once the runtime's initial
    // extension load (list + every package) has completed. is_ready() alone
    // flips true as soon as the list exists, before the packages are loaded.
    std::shared_ptr<bool> extensions_ready = std::make_shared<bool>(false);
};
static std::map<int64_t, ProfileExtensionHosts> g_profile_hosts;
static std::map<HWND, std::unique_ptr<orion::win::WebViewHost>> g_web_view_hosts;
static std::shared_ptr<orion::bridge::WebViewFactoryBridge> activateProfileWorld(int64_t profile_id);
static WebExtensions::WebExtensionsRuntime* readyRuntimeForProfile(int64_t profile_id);
static void shutdownExtensionHosts();

static std::shared_ptr<orion::SyncService> g_sync_service;
static orion::SyncService::OperationHandle g_sync_handle;

// Returns the loaded engine's EngineId value, or -1 before LoadBrowserEngine.
extern "C" __declspec(dllexport) int GetBrowserEngine()
{
    BrowserEngine* engine = orionLoadedEngine();
    return engine ? static_cast<int>(engine->engineId()) : -1;
}

// Returns -1 if this is the main browser process (continue normal startup).
// Returns >= 0 if this is an engine subprocess (exit with this code).
// Only CEF child processes carry a --type= switch on the command line; the
// main process must not load any engine plugin here. The switch must be
// matched as a whole argument, not a substring: a URL handed to the browser
// (protocol handler, file association) can legitimately contain "--type="
// and must not be mistaken for a subprocess launch.
extern "C" __declspec(dllexport) int CheckBrowserSubprocess()
{
    bool is_subprocess = false;
    int argc = 0;
    if (LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc)) {
        for (int i = 1; i < argc && !is_subprocess; ++i)
            is_subprocess = wcsncmp(argv[i], L"--type=", 7) == 0;
        LocalFree(argv);
    }
    if (!is_subprocess)
        return -1;
    std::string err;
    BrowserEngine* engine = orionLoadEngine("blink", err);
    if (!engine)
        return 1;  // we ARE a CEF child; -1 would boot a second full browser
    return engine->checkBrowserSubprocess();
}

extern "C" __declspec(dllexport) const char*
GetOrionVersion()
{
    return ReturnString(ORION_VERSION);
}

// The registry id of the profile this process hosts (one profile per
// process). Set by InitOrionCore; 0 before that.
static int64_t g_profile_id = 0;

extern "C" __declspec(dllexport) int64_t GetCurrentProfileId()
{
    return g_profile_id;
}

// Resolves which profile this process should host, running the legacy DB
// split / registry seeding if needed so the answer is authoritative on the
// very first launch too. requested_id 0 = the default profile. Returns the
// resolved registry id, or 0 on failure (unknown id, migration failure).
// Safe to call before InitOrionCore (no core, no run loop); InitOrionCore
// repeats the (idempotent) migration steps itself.
extern "C" __declspec(dllexport) int64_t ResolveStartupProfile(int64_t requested_id)
{
    orion::CommonDb& common = orion::CommonDb::instance();
    if (!common.open() || !common.migrate_legacy_if_needed())
        return 0;
    if (common.get_profiles().empty()
        && common.create_profile("Default", "", DefaultBrowserEngine) == 0)
        return 0;
    if (requested_id != 0)
        return common.get_profile(requested_id) ? requested_id : 0;
    std::optional<orion::RegistryProfile> def = common.get_default_profile();
    return def ? def->id : 0;
}

// Must be called once on the UI thread before any other exports.
// The UI thread becomes the orion main thread.
// profile_id: registry id of the profile this process hosts; 0 = the
// registry's default profile. Opens (provisioning or splitting from the
// legacy single DB when needed) that profile's own database at
// data\Profiles\<id>\profile.db — core sees exactly one real profile.
// After this returns, call GetWakeupEventHandle() to obtain the event that is
// signalled whenever the run loop has work — use it to drive TickOrionCore().
extern "C" __declspec(dllexport) const char* InitOrionCore(int64_t profile_id)
{
    // Auto-reset event: WaitForSingleObject on this wakes up immediately whenever
    // a background thread posts work to the main loop via RunLoop::wakeup().
    g_wakeup_event = CreateEventW(NULL, FALSE, FALSE, NULL);

    orion::CommonDb& common = orion::CommonDb::instance();
    if (!common.open())
        return ReturnString("Error: cannot open the common database (orion.db)");
    if (!common.migrate_legacy_if_needed())
        return ReturnString("Error: splitting the legacy profile database failed — see log");

    // Fresh install: seed the registry with a default profile.
    if (common.get_profiles().empty()
        && common.create_profile("Default", "", DefaultBrowserEngine) == 0)
        return ReturnString("Error: cannot create the default profile");

    std::optional<orion::RegistryProfile> profile = profile_id != 0
        ? common.get_profile(profile_id)
        : common.get_default_profile();
    if (!profile)
        return ReturnString("Error: profile " + std::to_string(profile_id) + " does not exist");
    g_profile_id = profile->id;

    const std::filesystem::path db_path = orion::CommonDb::profile_db_path(profile->id);
    std::error_code ec;
    std::filesystem::create_directories(db_path.parent_path(), ec);
    const bool fresh_profile_db = !std::filesystem::exists(db_path);

    g_core_api = orion::CoreApi::create("com.kagi.orion");
#ifdef ORION_TEST_EXTENSIONS
    // Test-extension builds run the core in debug mode (orion-linux: --debug):
    // the embedded debug extension list is offered and the test harness's
    // "debug" manifest permission can be granted. Must precede init().
    g_core_api->set_debug_mode(true);
#endif
    g_core_api->set_password_storage(&g_dpapi_password_storage);
    g_core_api->set_notification_provider(&g_host_notification_provider);
    orion::Error err = g_core_api->init(db_path.string());
    if (err) return ReturnString("Error: " + err.message());

    // Hook into RunLoop::wakeup() so every background-thread wakeup immediately
    // signals the event, letting the C# watcher thread pump without delay.
    orion::tls_run_loop()->set_wakeup_hook([]() {
        SetEvent(g_wakeup_event);
    });

    // First launch of this profile: core migrations just created an empty DB
    // (internal profile only). Create the real profile row carrying the GLOBAL
    // registry id: pre-seed sqlite's rowid assignment (max+1) so core's
    // create_profile allocates exactly profile->id along with all its default
    // rows (bookmark folders, shared-pinned-tabs window, settings, toolbar).
    if (fresh_profile_db) {
        if (!orion::CommonDb::preseed_profile_rowid(db_path, profile->id))
            return ReturnString("Error: cannot provision the profile database");
        orion::CreateProfileQuery q;
        q.name = profile->name;
        q.icon_name = profile->icon;
        int64_t created_id = 0;
        bool done = false;
        g_core_api->create_profile(q, [&](orion::CreateProfileReply reply) {
            if (!reply.err)
                created_id = (int64_t)reply.id;
            done = true;
        });
        while (!done) orion::tls_run_loop()->pump();
        orion::CommonDb::remove_preseed_row(db_path, profile->id);
        if (created_id != profile->id) {
            orion::log_error("InitOrionCore: provisioned id %lld != registry id %lld",
                (long long)created_id, (long long)profile->id);
            return ReturnString("Error: profile database provisioning id mismatch");
        }
        // Keep core's in-DB notion of "default profile" pointing at the only
        // real profile in this file (the registry stays the authority).
        bool set_done = false;
        g_core_api->set_default_profile(orion::ProfileId(profile->id),
            [&](orion::Error) { set_done = true; });
        while (!set_done) orion::tls_run_loop()->pump();
    }

    g_core_api->prepare_run_loop();
    return ReturnString("OK");
}

// Effective UI appearance pushed by the shell. Latched so a push before
// LoadBrowserEngine isn't dropped; LoadBrowserEngine re-applies it.
static bool g_useDarkAppearance = false;

// Loads the engine plugin ("webkit" or "blink") and binds its script bridge
// to the core. Call after InitOrionCore and before the first CreateOrionWindow;
// a second call with a different engine fails — one engine per process.
extern "C" __declspec(dllexport) const char* LoadBrowserEngine(const char* name)
{
    if (!g_core_api)
        return ReturnString("Error: InitOrionCore must be called first");
    std::string err;
    BrowserEngine* engine = orionLoadEngine(name, err);
    if (!engine)
        return ReturnString("Error: " + err);
    g_script_bridge = engine->createScriptBridge();
    if (!g_script_bridge)
        orion::log_info("LoadBrowserEngine: engine '%s' has no script bridge; WebExtensions disabled", name);
    engine->setUseDarkAppearance(g_useDarkAppearance);
    return ReturnString("OK");
}

extern "C" __declspec(dllexport) void SetUseDarkAppearance(int32_t useDarkAppearance)
{
    g_useDarkAppearance = useDarkAppearance != 0;
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->setUseDarkAppearance(g_useDarkAppearance);
}

// Returns the Win32 auto-reset event HANDLE that is signalled whenever the orion
// run loop has work pending. Use WaitForSingleObject on this handle (with a ~100 ms
// timeout as fallback for timer-based work) and call TickOrionCore() on the UI
// thread when it fires.
extern "C" __declspec(dllexport) HANDLE GetWakeupEventHandle()
{
    return g_wakeup_event;
}

// Pumps the orion run loop once. Must be called on the UI thread.
// Returns 1 while running, 0 once the core has fully stopped — do not call again after 0.
extern "C" __declspec(dllexport) int TickOrionCore()
{
    if (!orion::tls_run_loop()->pump()) {
        g_core_api.reset();
        return 0;
    }
    return 1;
}

// Triggers a graceful shutdown. TickOrionCore() will return 0 shortly after.
extern "C" __declspec(dllexport) void ShutdownOrionCore()
{
    if (!g_core_api) return;
    // Detach extension hosts (views, worlds, profiles) before destroying
    // browser views; the core must outlive all of them.
    shutdownExtensionHosts();

    if (g_sync_handle)
        g_sync_handle->cancel();
    g_sync_handle.reset();
    g_sync_service.reset();
    // Destroy remaining browser views before stopping the core so no
    // callbacks (title changes, URL changes etc.) can fire during core shutdown.
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->cleanBrowserEngine();
    orion::Error err = g_core_api->request_stop_run_loop([]() {});
    assert(!err && "request_stop_run_loop failed");
}

extern "C" __declspec(dllexport) void AddHistoryEntry(int64_t profile_id, const char* url, const char* title)
{
    if (!g_core_api || profile_id == 0) return;
    orion::CreateHistoryEntryQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.address    = url;
    q.website    = (title && *title) ? title : url;
    q.visited_at = orion::UnixTimeUtc64(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
    g_core_api->create_history_entry(q, [](orion::CreateHistoryEntryReply) {});
}

extern "C" __declspec(dllexport) HistoryEntryNative* GetRecentHistory(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;
    auto now = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();

    // Step 1: fetch IDs ordered by visited_at DESC
    std::vector<orion::HistoryEntryId> ids;
    bool ids_done = false;
    orion::GetHistoryEntryIdsQuery q;
    q.profile_id     = orion::ProfileId(profile_id);
    q.visited_after  = orion::UnixTimeUtc64(0);
    q.visited_before = orion::UnixTimeUtc64(now + 1);
    g_core_api->get_history_entry_ids(q, [&](orion::GetHistoryEntryIdsReply reply) {
        ids = reply.err ? std::vector<orion::HistoryEntryId>{} : reply.ids;
        ids_done = true;
    });
    while (!ids_done) orion::tls_run_loop()->pump();
    if (ids.empty()) return nullptr;

    // Step 2: fetch full entries
    std::vector<orion::HistoryEntryDto> entries;
    bool entries_done = false;
    g_core_api->get_history_entries(ids, [&](orion::GetHistoryEntriesReply reply) {
        entries = reply.err ? std::vector<orion::HistoryEntryDto>{} : reply.entries;
        entries_done = true;
    });
    while (!entries_done) orion::tls_run_loop()->pump();

    int count = (int)entries.size();
    if (count == 0) return nullptr;

    // Single allocation: struct array followed by UTF-8 string data.
    // char* fields point into the same buffer; one free() releases everything.
    size_t blob_size = count * sizeof(HistoryEntryNative);
    for (int i = 0; i < count; i++) {
        blob_size += entries[i].website.size() + 1;
        blob_size += entries[i].address.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<HistoryEntryNative*>(base);
    char* str = base + count * sizeof(HistoryEntryNative);

    for (int i = 0; i < count; i++) {
        arr[i].id         = (int64_t)entries[i].id;
        arr[i].profile_id = (int64_t)entries[i].profile_id;
        arr[i].visited_at = (int64_t)entries[i].visited_at;

        arr[i].website = str;
        memcpy(str, entries[i].website.c_str(), entries[i].website.size() + 1);
        str += entries[i].website.size() + 1;

        arr[i].address = str;
        memcpy(str, entries[i].address.c_str(), entries[i].address.size() + 1);
        str += entries[i].address.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeHistoryEntries(HistoryEntryNative* entries)
{
    free(entries); // struct array and all string data are one allocation
}

extern "C" __declspec(dllexport) HistoryEntryNative* GetHistoryForMenu(int64_t profile_id, int days, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    orion::ProfileHistoryQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.search_filter = "length(address) > 0";
    q.offset = 0;
    q.limit = days;

    std::vector<orion::HistoryEntryDto> entries;
    bool done = false;
    g_core_api->get_history_entries(q, [&](orion::GetHistoryEntriesReply reply) {
        entries = reply.err ? std::vector<orion::HistoryEntryDto>{} : reply.entries;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)entries.size();
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(HistoryEntryNative);
    for (int i = 0; i < count; i++) {
        blob_size += entries[i].website.size() + 1;
        blob_size += entries[i].address.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<HistoryEntryNative*>(base);
    char* str = base + count * sizeof(HistoryEntryNative);

    for (int i = 0; i < count; i++) {
        arr[i].id         = (int64_t)entries[i].id;
        arr[i].profile_id = (int64_t)entries[i].profile_id;
        arr[i].visited_at = (int64_t)entries[i].visited_at;

        arr[i].website = str;
        memcpy(str, entries[i].website.c_str(), entries[i].website.size() + 1);
        str += entries[i].website.size() + 1;

        arr[i].address = str;
        memcpy(str, entries[i].address.c_str(), entries[i].address.size() + 1);
        str += entries[i].address.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void ClearHistory(int64_t profile_id)
{
    if (!g_core_api || profile_id == 0) return;
    orion::ProfileHistoryQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    bool done = false;
    g_core_api->clear_history_entries(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void DeleteHistoryEntries(const int64_t* ids, int count)
{
    if (!g_core_api || count <= 0) return;
    std::vector<orion::HistoryEntryId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::HistoryEntryId(ids[i]));
    bool done = false;
    g_core_api->delete_history_entries(id_vec, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) BookmarkFolderNative* GetBookmarkFolders(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    std::vector<orion::BookmarkFolderDto> folders;
    bool done = false;
    g_core_api->get_bookmark_folders_by_query(orion::ProfileId(profile_id), [&](orion::GetBookmarkFoldersReply reply) {
        folders = reply.err ? std::vector<orion::BookmarkFolderDto>{} : reply.folders;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)folders.size();
    if (count == 0)
        return nullptr;

    size_t blob_size = count * sizeof(BookmarkFolderNative);
    for (int i = 0; i < count; i++)
        blob_size += folders[i].name.size() + 1;

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<BookmarkFolderNative*>(base);
    char* str = base + count * sizeof(BookmarkFolderNative);

    for (int i = 0; i < count; i++) {
        arr[i].id               = (int64_t)folders[i].id;
        arr[i].profile_id       = (int64_t)folders[i].profile_id;
        arr[i].is_internal      = (int8_t)folders[i].is_internal;
        arr[i].parent_folder_id = (int64_t)folders[i].parent_folder_id;
        arr[i].seq_num          = folders[i].seq_num;
        arr[i].type             = (int32_t)folders[i].type;
        arr[i].is_expanded      = (int8_t)folders[i].is_expanded;

        arr[i].name = str;
        memcpy(str, folders[i].name.c_str(), folders[i].name.size() + 1);
        str += folders[i].name.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeBookmarkFolders(BookmarkFolderNative* folders)
{
    free(folders);
}

extern "C" __declspec(dllexport) BookmarkNative* GetBookmarks(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    orion::GetBookmarksByQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    std::vector<orion::BookmarkDto> bookmarks;
    bool done = false;
    g_core_api->get_bookmarks_by_query(q, [&](orion::GetBookmarksReply reply) {
        bookmarks = reply.err ? std::vector<orion::BookmarkDto>{} : reply.bookmarks;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)bookmarks.size();
    if (count == 0)
        return nullptr;

    size_t blob_size = count * sizeof(BookmarkNative);
    for (int i = 0; i < count; i++) {
        blob_size += bookmarks[i].name.size() + 1;
        blob_size += bookmarks[i].address.size() + 1;
        blob_size += bookmarks[i].invocation_keyword.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<BookmarkNative*>(base);
    char* str = base + count * sizeof(BookmarkNative);

    for (int i = 0; i < count; i++) {
        arr[i].id               = (int64_t)bookmarks[i].id;
        arr[i].profile_id       = (int64_t)bookmarks[i].profile_id;
        arr[i].parent_folder_id = (int64_t)bookmarks[i].parent_folder_id;
        arr[i].seq_num          = bookmarks[i].seq_num;

        arr[i].name = str;
        memcpy(str, bookmarks[i].name.c_str(), bookmarks[i].name.size() + 1);
        str += bookmarks[i].name.size() + 1;

        arr[i].address = str;
        memcpy(str, bookmarks[i].address.c_str(), bookmarks[i].address.size() + 1);
        str += bookmarks[i].address.size() + 1;

        arr[i].invocation_keyword = str;
        memcpy(str, bookmarks[i].invocation_keyword.c_str(), bookmarks[i].invocation_keyword.size() + 1);
        str += bookmarks[i].invocation_keyword.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeBookmarks(BookmarkNative* bookmarks)
{
    free(bookmarks);
}

extern "C" __declspec(dllexport) BookmarkNative* GetRecentBookmarks(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    // Step 1: fetch recent bookmark IDs
    std::vector<orion::BookmarkId> ids;
    bool ids_done = false;
    orion::GetRecentBookmarkIdsQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    g_core_api->get_recent_bookmark_ids(q, [&](orion::GetRecentBookmarkIdsReply reply) {
        ids = reply.err ? std::vector<orion::BookmarkId>{} : reply.ids;
        ids_done = true;
    });
    while (!ids_done) orion::tls_run_loop()->pump();
    if (ids.empty()) return nullptr;

    // Step 2: fetch full bookmark entries, ordered by created_at descending
    std::vector<orion::BookmarkDto> bookmarks;
    bool entries_done = false;
    orion::GetBookmarksQuery bq;
    bq.ids = ids;
    bq.order_by = orion::BookmarkOrderByMode::created_at_desc;
    g_core_api->get_bookmarks(bq, [&](orion::GetBookmarksReply reply) {
        bookmarks = reply.err ? std::vector<orion::BookmarkDto>{} : reply.bookmarks;
        entries_done = true;
    });
    while (!entries_done) orion::tls_run_loop()->pump();

    int count = (int)bookmarks.size();
    if (count == 0) return nullptr;

    // Single allocation: struct array followed by UTF-8 string data.
    size_t blob_size = count * sizeof(BookmarkNative);
    for (int i = 0; i < count; i++) {
        blob_size += bookmarks[i].name.size() + 1;
        blob_size += bookmarks[i].address.size() + 1;
        blob_size += bookmarks[i].invocation_keyword.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<BookmarkNative*>(base);
    char* str = base + count * sizeof(BookmarkNative);

    for (int i = 0; i < count; i++) {
        arr[i].id               = (int64_t)bookmarks[i].id;
        arr[i].profile_id       = (int64_t)bookmarks[i].profile_id;
        arr[i].parent_folder_id = (int64_t)bookmarks[i].parent_folder_id;
        arr[i].seq_num          = bookmarks[i].seq_num;

        arr[i].name = str;
        memcpy(str, bookmarks[i].name.c_str(), bookmarks[i].name.size() + 1);
        str += bookmarks[i].name.size() + 1;

        arr[i].address = str;
        memcpy(str, bookmarks[i].address.c_str(), bookmarks[i].address.size() + 1);
        str += bookmarks[i].address.size() + 1;

        arr[i].invocation_keyword = str;
        memcpy(str, bookmarks[i].invocation_keyword.c_str(), bookmarks[i].invocation_keyword.size() + 1);
        str += bookmarks[i].invocation_keyword.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void MoveBookmark(
    int64_t id, int64_t parent_folder_id, int64_t next_bookmark_id, int64_t next_folder_id)
{
    if (!g_core_api) return;

    orion::MoveBookmarkQuery q;
    q.bookmark_id              = orion::BookmarkId(id);
    q.target_parent_folder_id  = orion::BookmarkFolderId(parent_folder_id);
    q.next_bookmark_id         = orion::BookmarkId(next_bookmark_id);
    q.next_folder_id           = orion::BookmarkFolderId(next_folder_id);

    bool done = false;
    g_core_api->move_bookmark(q, [&](orion::Error err) {
        if (err)
            orion::log_error("MoveBookmark: %s", err.message().c_str());
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void MoveBookmarkFolder(
    int64_t id, int64_t parent_folder_id, int64_t next_bookmark_id, int64_t next_folder_id)
{
    if (!g_core_api) return;

    orion::MoveBookmarkFolderQuery q;
    q.folder_id                = orion::BookmarkFolderId(id);
    q.target_parent_folder_id  = orion::BookmarkFolderId(parent_folder_id);
    q.next_bookmark_id         = orion::BookmarkId(next_bookmark_id);
    q.next_folder_id           = orion::BookmarkFolderId(next_folder_id);

    bool done = false;
    g_core_api->move_bookmark_folder(q, [&](orion::Error err) {
        if (err)
            orion::log_error("MoveBookmarkFolder: %s", err.message().c_str());
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void DeleteBookmark(int64_t id)
{
    if (!g_core_api) return;
    g_core_api->delete_bookmarks({orion::BookmarkId(id)}, [](orion::Error) {});
}

extern "C" __declspec(dllexport) void DeleteBookmarkFolder(int64_t id)
{
    if (!g_core_api) return;
    g_core_api->delete_bookmark_folders({orion::BookmarkFolderId(id)}, [](orion::Error) {});
}

extern "C" __declspec(dllexport) int64_t CreateBookmarkFolder(
    int64_t profile_id, const char* name, int64_t parent_folder_id)
{
    if (!g_core_api || profile_id == 0) return 0;

    orion::CreateBookmarkFolderQuery q;
    q.profile_id       = orion::ProfileId(profile_id);
    q.name             = name;
    q.parent_folder_id = orion::BookmarkFolderId(parent_folder_id);

    int64_t result = 0;
    bool done = false;
    g_core_api->create_bookmark_folder(q, [&](orion::CreateBookmarkFolderReply reply) {
        result = reply.err ? 0LL : (int64_t)reply.folder_id;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result;
}

extern "C" __declspec(dllexport) int64_t CreateBookmark(
    int64_t profile_id, const char* name, const char* address, int64_t parent_folder_id)
{
    if (!g_core_api || profile_id == 0) return 0;

    orion::CreateBookmarkQuery q;
    q.profile_id       = orion::ProfileId(profile_id);
    q.name             = name;
    q.address          = address;
    q.parent_folder_id = orion::BookmarkFolderId(parent_folder_id);

    int64_t result = 0;
    bool done = false;
    g_core_api->create_bookmark(q, [&](orion::CreateBookmarkReply reply) {
        result = reply.err ? 0LL : (int64_t)reply.bookmark_id;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result;
}

extern "C" __declspec(dllexport) HANDLE GetCefWakeupEventHandle()
{
    BrowserEngine* engine = orionLoadedEngine();
    return engine ? engine->getCefWakeupEventHandle() : nullptr;
}

extern "C" __declspec(dllexport) void PumpCefMessageLoop()
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->pumpCefMessageLoop();
}

extern "C" __declspec(dllexport) HWND CreateOrionWindow(HWND parentWindow, BrowserCallbacks browserCallbacks, int64_t profileId)
{
    BrowserEngine* engine = orionLoadedEngine();
    if (!engine) {
        orion::log_error("CreateOrionWindow: no browser engine loaded");
        return nullptr;
    }
    return engine->createBrowserView(parentWindow, browserCallbacks, profileId, nullptr);
}

extern "C" __declspec(dllexport) HWND CreateOrionWindowWithUrl(HWND parentWindow, BrowserCallbacks browserCallbacks, int64_t profileId, const char* url)
{
    BrowserEngine* engine = orionLoadedEngine();
    if (!engine) {
        orion::log_error("CreateOrionWindowWithUrl: no browser engine loaded");
        return nullptr;
    }
    return engine->createBrowserView(parentWindow, browserCallbacks, profileId, url);
}

// Popup/OpenURL callbacks (set once by C# during app init); engines read
// them via the host_api.h getters.
static CreatePopupCallback g_createPopupCallback = nullptr;
static PopupReadyCallback g_popupReadyCallback = nullptr;
static OpenURLCallback g_openURLCallback = nullptr;

CreatePopupCallback getCreatePopupCallback() { return g_createPopupCallback; }
PopupReadyCallback getPopupReadyCallback() { return g_popupReadyCallback; }
OpenURLCallback getOpenURLCallback() { return g_openURLCallback; }

extern "C" __declspec(dllexport) void SetCreatePopupCallback(CreatePopupCallback cb)
{
    g_createPopupCallback = cb;
}

extern "C" __declspec(dllexport) void SetPopupReadyCallback(PopupReadyCallback cb)
{
    g_popupReadyCallback = cb;
}

extern "C" __declspec(dllexport) void SetOpenURLCallback(OpenURLCallback cb)
{
    g_openURLCallback = cb;
}

extern "C" __declspec(dllexport) void DestroyOrionWindow(HWND hwnd)
{
    // Detach the extension host first so the core drops its view bridge
    // while the native view still exists.
    g_web_view_hosts.erase(hwnd);
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->destroyBrowserView(hwnd);
}

extern "C" __declspec(dllexport) void ResizeOrionWindow(HWND hwnd, int width, int height)
{
    if (hwnd && IsWindow(hwnd))
        SetWindowPos(hwnd, nullptr, 0, 0, width, height,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
}


extern "C" __declspec(dllexport) HRESULT LoadURL(HWND hwnd, const char* url)
{
    BrowserEngine* engine = orionLoadedEngine();
    return engine ? engine->loadURL(hwnd, url) : E_HANDLE;
}

// Asks the page to leave HTML5 element fullscreen (UI-initiated exit, e.g.
// F11 or tab switch). The engine confirms via the elementFullscreenChanged
// browser callback; no-op when the view is not in element fullscreen.
extern "C" __declspec(dllexport) void RequestExitElementFullscreen(HWND hwnd)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->requestExitElementFullscreen(hwnd);
}

extern "C" __declspec(dllexport) void LoadHTMLString(HWND hwnd, const char* html, const char* unreachableUrl)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->loadHTMLString(hwnd, html ? html : "", unreachableUrl ? unreachableUrl : "");
}

extern "C" __declspec(dllexport) void ExecuteJavaScript(HWND hwnd, const char* script)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->executeJavaScript(hwnd, script ? script : "");
}

extern "C" __declspec(dllexport) void Reload(HWND hwnd)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->reload(hwnd);
}

extern "C" __declspec(dllexport) void ReloadBypassingCache(HWND hwnd)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->reloadBypassingCache(hwnd);
}

extern "C" __declspec(dllexport) void Forward(HWND hwnd)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->forward(hwnd);
}

extern "C" __declspec(dllexport) void Backward(HWND hwnd)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->backward(hwnd);
}

// Minimum page font size in px; 0 = no minimum (settings::browsing::
// minimum_font_size gated on enable_minimum_font_size, applied by C#).
extern "C" __declspec(dllexport) void SetMinimumFontSize(int32_t size)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->setMinimumFontSize(size);
}

// settings::websites::javascript_setting_for_all_websites, applied by C#.
// Affects loads from the next navigation on; running scripts are unaffected.
extern "C" __declspec(dllexport) void SetJavaScriptEnabled(int32_t enabled)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->setJavaScriptEnabled(enabled != 0);
}

// Engine-native popup blocking (Chromium's built-in blocker; no-op on
// WebKit). C# disables it whenever the app's popup setting owns the policy.
extern "C" __declspec(dllexport) void SetNativePopupBlocking(int32_t enabled)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->setNativePopupBlocking(enabled != 0);
}

extern "C" __declspec(dllexport) void SetCustomUserAgent(HWND hwnd, const wchar_t* userAgent)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->setCustomUserAgent(hwnd, userAgent);
}

// Process-default user agent from the profile's setting. Call after
// LoadBrowserEngine and before the first CreateOrionWindow — CEF latches it
// at engine initialization.
extern "C" __declspec(dllexport) void SetDefaultUserAgent(const wchar_t* userAgent)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->setDefaultUserAgent(userAgent);
}

extern "C" __declspec(dllexport) void ExecuteContextMenuCommand(HWND hwnd, int32_t command, const char* arg, int32_t x, int32_t y)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->executeContextMenuCommand(hwnd, static_cast<WebContextMenuCommand>(command), arg, x, y);
}

// Opens (or focuses) the view's DevTools frontend; the frontend view arrives
// via BrowserCallbacks.devToolsViewChanged. x/y >= 0 inspect the element at
// those view coordinates.
extern "C" __declspec(dllexport) void ShowDevTools(HWND hwnd, int32_t x, int32_t y)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->showDevTools(hwnd, x, y);
}

// Closes the view's DevTools frontend; devToolsViewChanged(nullptr) follows.
extern "C" __declspec(dllexport) void CloseDevTools(HWND hwnd)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->closeDevTools(hwnd);
}

extern "C" __declspec(dllexport) void FindInPage(HWND hwnd, const char* text, int32_t forward, int32_t findNext)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->findInPage(hwnd, text ? text : "", forward != 0, findNext != 0);
}

extern "C" __declspec(dllexport) void StopFindInPage(HWND hwnd, int32_t clearSelection)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->stopFindInPage(hwnd, clearSelection != 0);
}

// Requests an async snapshot of the view's visible viewport (JPEG, at most
// maxPixelWidth wide; 0 = native size). The result arrives via the view's
// snapshotTaken callback; nullptr/0 on failure. Only reliable while the
// view is (or was just) visible — hidden views can't paint.
extern "C" __declspec(dllexport) void RequestTabSnapshot(HWND hwnd, int32_t maxPixelWidth)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->requestTabSnapshot(hwnd, maxPixelWidth);
}

// Resolves a media-access request raised via a view's
// mediaAccessPermissionRequested callback. allow grants exactly what the
// page requested; ids whose view is already gone are ignored.
extern "C" __declspec(dllexport) void RespondToMediaAccessPermissionRequest(int64_t requestId, int32_t allow)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->respondToMediaAccessPermission(requestId, allow != 0);
}

// Resolves an HTTP-auth challenge raised via a view's
// authCredentialsRequested callback. allow=0 cancels the challenge (the
// site's 401 body renders); ids whose view is already gone are ignored.
extern "C" __declspec(dllexport) void RespondToAuthCredentials(
    int64_t requestId, int32_t allow, const wchar_t* username, const wchar_t* password)
{
    if (BrowserEngine* engine = orionLoadedEngine())
        engine->respondToAuthCredentials(requestId, allow != 0, username, password);
}

// ---------------------------------------------------------------------------
// Profile registry (orion.db). One profile per process: enumeration and
// CRUD go to the cross-profile registry, never to this process's core DB.
// A new profile's own database is provisioned on its first launch
// (InitOrionCore); deletion marks the row, and ProfileDataCleanup (C#)
// sweeps the data dirs.
// ---------------------------------------------------------------------------

extern "C" __declspec(dllexport) void SetDefaultProfile(int64_t profile_id)
{
    orion::CommonDb::instance().set_default_profile(profile_id);
}

extern "C" __declspec(dllexport) ProfileNative* GetProfiles(int* outCount)
{
    *outCount = 0;
    std::vector<orion::RegistryProfile> profiles = orion::CommonDb::instance().get_profiles();

    int count = (int)profiles.size();
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(ProfileNative);
    for (int i = 0; i < count; i++) {
        blob_size += profiles[i].name.size() + 1;
        blob_size += profiles[i].icon.size() + 1;
        blob_size += profiles[i].keyboard_shortcut.size() + 1;
        blob_size += profiles[i].engine.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<ProfileNative*>(base);
    char* str = base + count * sizeof(ProfileNative);

    auto copy_str = [&](const std::string& s) -> char* {
        char* dest = str;
        memcpy(str, s.c_str(), s.size() + 1);
        str += s.size() + 1;
        return dest;
    };

    for (int i = 0; i < count; i++) {
        arr[i].id                   = profiles[i].id;
        arr[i].name                 = copy_str(profiles[i].name);
        arr[i].icon_name            = copy_str(profiles[i].icon);
        arr[i].show_in_address_bar  = profiles[i].show_in_address_bar ? 1 : 0;
        arr[i].show_badge_in_app_icon = profiles[i].show_badge_in_app_icon ? 1 : 0;
        arr[i].keyboard_shortcut    = copy_str(profiles[i].keyboard_shortcut);
        arr[i].is_internal          = 0;  // the registry holds only real profiles
        arr[i].is_default           = profiles[i].is_default ? 1 : 0;
        arr[i].engine               = copy_str(profiles[i].engine);
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeProfiles(ProfileNative* profiles)
{
    free(profiles);
}

extern "C" __declspec(dllexport) int64_t CreateProfile(
    const char* name, const char* icon_name, const char* engine)
{
    return orion::CommonDb::instance().create_profile(
        name ? name : "", icon_name ? icon_name : "", engine ? engine : DefaultBrowserEngine);
}

extern "C" __declspec(dllexport) void UpdateProfile(
    int64_t id, const char* name, const char* icon_name,
    int8_t show_in_address_bar, int8_t show_badge_in_app_icon,
    const char* keyboard_shortcut, const char* engine)
{
    orion::RegistryProfile p;
    p.id = id;
    p.name = name ? name : "";
    p.icon = icon_name ? icon_name : "";
    p.engine = engine && *engine ? engine : DefaultBrowserEngine;
    p.show_in_address_bar = show_in_address_bar != 0;
    p.show_badge_in_app_icon = show_badge_in_app_icon != 0;
    p.keyboard_shortcut = keyboard_shortcut ? keyboard_shortcut : "";
    orion::CommonDb::instance().update_profile(p);
}

extern "C" __declspec(dllexport) bool DeleteProfile(int64_t id)
{
    return orion::CommonDb::instance().delete_profile(id);
}

// Removes a deleted profile's data dirs; called by a *surviving* process
// (ProfileDataCleanup) — a process cannot remove its own dirs: its handles
// release only at termination, and its engine children exit after it. The
// bounded unlock wait bridges the gap between the dying process releasing
// its single-instance claim and its handles/children actually going away.
extern "C" __declspec(dllexport) void DeleteProfileData(int64_t id)
{
    deleteProfileData(id, 5000);
}

// Ids of deleted profiles — their rows are kept so ids are never reused, and
// they mark data dirs owed deletion. C# sweeps each id via DeleteProfileData,
// waiting out a still-exiting owner. CoTaskMemAlloc'd; the CLR caller frees.
extern "C" __declspec(dllexport) int64_t* GetDeletedProfileIds(int32_t* out_count)
{
    std::vector<int64_t> ids = orion::CommonDb::instance().deleted_profile_ids();
    *out_count = static_cast<int32_t>(ids.size());
    if (ids.empty())
        return nullptr;
    auto* buf = static_cast<int64_t*>(CoTaskMemAlloc(ids.size() * sizeof(int64_t)));
    memcpy(buf, ids.data(), ids.size() * sizeof(int64_t));
    return buf;
}

extern "C" __declspec(dllexport) void MoveProfileEntry(int64_t profile_id, int64_t next_profile_id)
{
    orion::CommonDb::instance().move_profile(profile_id, next_profile_id);
}

// App-wide settings shared by every profile (orion.db). Distinct from the
// per-profile Get/SetSetting exports, which target this profile's core DB.
extern "C" __declspec(dllexport) const char* GetAppSetting(
    const char* key, const char* default_value)
{
    std::optional<std::string> value =
        orion::CommonDb::instance().get_app_setting(key ? key : "");
    return ReturnString(value.value_or(default_value ? default_value : ""));
}

extern "C" __declspec(dllexport) void SetAppSetting(
    const char* key, const char* value, const char* type)
{
    orion::CommonDb::instance().set_app_setting(
        key ? key : "", value ? value : "", type && *type ? type : "text");
}

// Returns the setting value as a UTF-8 string, or default_value if not set.
// type: "text" | "integer" | "boolean" | "real"
// Integers/enums are stored as decimal strings; booleans as "1"/"0".
extern "C" __declspec(dllexport) const char* GetSetting(
    int64_t profile_id,
    const char* key, const char* realm, const char* key_name,
    const char* type, const char* default_value)
{
    if (!g_core_api || profile_id == 0)
        return ReturnString(default_value ? default_value : "");

    orion::GetSettingQuery query{};
    query.profile_id = orion::ProfileId(profile_id);
    query.key = key;
    query.realm = realm;
    query.key_name = key_name;
    query.type = type;
    query.default_value = default_value ? default_value : "";

    std::string value;
    bool done = false;
    g_core_api->get_setting_raw(query,
        [&](orion::GetSettingReply<std::string> reply) {
            value = (reply.err || reply.value.empty()) ? default_value : reply.value;
            done = true;
        });
    while (!done) orion::tls_run_loop()->pump();
    return ReturnString(value);
}

extern "C" __declspec(dllexport) void SetSetting(
    int64_t profile_id,
    const char* key, const char* realm, const char* key_name,
    const char* type, const char* value, const char* default_value)
{
    if (!g_core_api || profile_id == 0) return;

    orion::SetSettingQuery query{};
    query.profile_id = orion::ProfileId(profile_id);
    query.key = key;
    query.realm = realm;
    query.key_name = key_name;
    query.type = type;
    query.value = value;
    query.default_value = default_value ? default_value : "";

    g_core_api->set_setting_raw(query, [](orion::SetSettingReply<std::string>) {});
}

// Per-website setting (settings::website::specific::<website>::<key_name>),
// falling back to the profile's corresponding settings::websites default.
// website is a page URL's authority (e.g. "meet.example.com"). Returns the
// raw stored representation (integers as decimal strings), or default_value
// on error.
extern "C" __declspec(dllexport) const char* GetSettingForWebsite(
    int64_t profile_id, const char* website, const char* key_name,
    const char* default_value)
{
    if (!g_core_api || profile_id == 0)
        return ReturnString(default_value ? default_value : "");

    orion::GetSettingForWebsiteOrDefaultQuery query{};
    query.profile_id = orion::ProfileId(profile_id);
    query.website = website ? website : "";
    query.key_name = key_name ? key_name : "";

    std::string value;
    bool done = false;
    g_core_api->get_setting_for_website_or_default(query,
        [&](orion::GetSettingForWebsiteOrDefaultReply reply) {
            value = (reply.err || reply.value.empty())
                ? (default_value ? default_value : "") : reply.value;
            done = true;
        });
    while (!done) orion::tls_run_loop()->pump();
    return ReturnString(value);
}

// Writes an integer-valued per-website setting (the same rows orion-linux's
// permission handlers write).
extern "C" __declspec(dllexport) void SetSettingForWebsite(
    int64_t profile_id, const char* website, const char* key_name,
    int64_t value, int64_t default_value)
{
    if (!g_core_api || profile_id == 0) return;

    g_core_api->set_setting_raw(g_core_api->create_set_setting_query_for_website_integer({
        .profile_id = orion::ProfileId(profile_id),
        .key_name = key_name ? key_name : "",
        .value = value,
        .default_value = default_value,
        .website = website ? website : "",
    }), [](orion::SetSettingReply<std::string>) {});
}

// Called on the UI thread after an observed setting's new value is committed.
// new_value uses the raw stored representation (booleans are "1"/"0"); the
// strings are only valid for the duration of the call.
typedef void(__cdecl* SettingChangedCallback)(int64_t profile_id, const char* key, const char* new_value);

// Observes changes to a single setting key across all profiles. Returns an
// observer id for RemoveSettingObserver, or 0 on failure. C# must keep the
// callback delegate alive while the observer is registered.
extern "C" __declspec(dllexport) int64_t AddSettingObserver(const char* key, SettingChangedCallback callback)
{
    if (!g_core_api || !key || !*key || !callback)
        return 0;

    std::string key_str = key;
    orion::ObserverId id = g_core_api->single_setting_change().add_observer(
        [callback](orion::SingleSettingAnyChange change) {
            callback(change.profile_id.value, change.key.c_str(), change.new_value.c_str());
        },
        [key_str](const orion::SingleSettingAnyChange& change) {
            return change.key == key_str;
        });
    return static_cast<int64_t>(id.value);
}

extern "C" __declspec(dllexport) void RemoveSettingObserver(int64_t observer_id)
{
    if (!g_core_api || observer_id == 0)
        return;
    g_core_api->single_setting_change().remove_observer(
        orion::ObserverId(static_cast<uint64_t>(observer_id)));
}

// -----------------------------------------------------------------------
// Reading List
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) ReadingListEntryNative* GetReadingList(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    std::vector<orion::ReadingListEntryDto> entries;
    bool done = false;
    orion::GetReadingListEntryIdsQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    g_core_api->get_reading_list_entries_by_query(q, [&](orion::GetReadingListEntriesReply reply) {
        entries = reply.err ? std::vector<orion::ReadingListEntryDto>{} : reply.entries;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)entries.size();
    if (count == 0) return nullptr;

    // Single allocation: struct array followed by UTF-8 string data.
    size_t blob_size = count * sizeof(ReadingListEntryNative);
    for (int i = 0; i < count; i++) {
        blob_size += entries[i].icon_name.size() + 1;
        blob_size += entries[i].title.size() + 1;
        blob_size += entries[i].address.size() + 1;
        blob_size += entries[i].referrer.size() + 1;
        blob_size += entries[i].description.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<ReadingListEntryNative*>(base);
    char* str = base + count * sizeof(ReadingListEntryNative);

    auto copy_str = [&](const std::string& s) -> char* {
        char* dest = str;
        memcpy(str, s.c_str(), s.size() + 1);
        str += s.size() + 1;
        return dest;
    };

    for (int i = 0; i < count; i++) {
        arr[i].id               = (int64_t)entries[i].id;
        arr[i].profile_id       = (int64_t)entries[i].profile_id;
        arr[i].added_on         = (int64_t)entries[i].added_on;
        arr[i].icon_name        = copy_str(entries[i].icon_name);
        arr[i].title            = copy_str(entries[i].title);
        arr[i].address          = copy_str(entries[i].address);
        arr[i].referrer         = copy_str(entries[i].referrer);
        arr[i].is_unread        = entries[i].is_unread ? 1 : 0;
        arr[i].is_saved_offline = entries[i].is_saved_offline ? 1 : 0;
        arr[i].description      = copy_str(entries[i].description);
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeReadingList(ReadingListEntryNative* entries)
{
    free(entries);
}

extern "C" __declspec(dllexport) int64_t AddReadingListEntry(int64_t profile_id, const char* title, const char* address)
{
    if (!g_core_api || profile_id == 0) return 0;

    orion::CreateReadingListEntryQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.title      = title;
    q.address    = address;
    q.added_on   = orion::UnixTimeUtc64(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());

    int64_t result_id = 0;
    bool done = false;
    g_core_api->create_reading_list_entry(q, [&](orion::CreateReadingListEntryReply reply) {
        if (!reply.err) result_id = (int64_t)reply.id;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result_id;
}

extern "C" __declspec(dllexport) void DeleteReadingListEntries(const int64_t* ids, int count)
{
    if (!g_core_api || count <= 0) return;
    std::vector<orion::ReadingListEntryId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::ReadingListEntryId(ids[i]));
    bool done = false;
    g_core_api->delete_reading_list_entries(id_vec, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void MarkReadingListEntryRead(int64_t id, int8_t is_unread)
{
    if (!g_core_api) return;
    orion::UpdateReadingListEntryQuery q;
    q.id = orion::ReadingListEntryId(id);
    q.is_unread = is_unread != 0;
    q.update_is_unread = true;
    bool done = false;
    g_core_api->update_reading_list_entry(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

// -----------------------------------------------------------------------
// Extensions
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) const char* InstallExtension(
    int64_t profile_id,
    const char* name, const char* displayName, const char* info,
    const char* installURL, const char* webExtType, const char* webExtID,
    const char* geckoId, const char* iconURL, const char* version,
    int8_t orionHosted,
    const char* filePath)
{
    if (!g_core_api || profile_id == 0) {
        orion::log_warning("InstallExtension: core not initialized or invalid profile id (%lld)", profile_id);
        return ReturnString("");
    }

    WebExtensions::ExtensionInfo ext_info;
    if (name && *name) ext_info.name = std::string(name);
    if (displayName && *displayName) ext_info.displayName = std::string(displayName);
    if (info && *info) ext_info.info = std::string(info);
    if (installURL && *installURL) ext_info.installURL = std::string(installURL);
    if (webExtType && *webExtType) ext_info.webExtType = std::string(webExtType);
    if (webExtID && *webExtID) ext_info.webExtID = std::string(webExtID);
    if (geckoId && *geckoId) ext_info.geckoId = std::string(geckoId);
    if (iconURL && *iconURL) ext_info.iconURL = std::string(iconURL);
    if (version && *version) ext_info.version = std::string(version);
    ext_info.orionHosted = orionHosted != 0;

    std::string file = (filePath && *filePath)
        ? std::string(filePath)
        : g_core_api->get_extension_fetch_path(ext_info);

    std::error_code ec;
    if (!std::filesystem::exists(file, ec)) {
        orion::log_warning("InstallExtension: extension package not found: %s", file.c_str());
        return ReturnString("");
    }

    auto sku  = g_core_api->internal_store().get_extension_sku(ext_info);
    auto type = g_core_api->internal_store().get_extension_type(ext_info);
    // The core resource cache is keyed by sku; extensions installed without a
    // webExtID/geckoId/installURL all share the fallback sku "0" and would
    // silently serve each other's resources.
    if (sku.value == "0")
        orion::log_warning("InstallExtension: '%s' has fallback sku \"0\" — "
            "resource cache collisions possible; pass a webExtID to avoid this",
            ext_info.name.value_or("").c_str());

    orion::InstallExtensionQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.sku        = sku;
    q.type       = type;
    q.file_path  = file;
    q.name       = ext_info.displayName.value_or(ext_info.name.value_or(""));
    q.description = ext_info.info.value_or("");
    q.icon_ref   = ext_info.iconURL.value_or("");

    std::string result_id;
    bool done = false;
    g_core_api->install_extension(q, [&](orion::InstallExtensionReply reply) {
        if (!reply.err)
            result_id = (std::string)reply.id;
        else
            orion::log_warning("InstallExtension: install of '%s' failed: %s",
                q.name.c_str(), reply.err.message().c_str());
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    return ReturnString(result_id);
}

extern "C" __declspec(dllexport) InstalledExtensionNative* GetInstalledExtensions(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    // Step 1: fetch IDs for the profile
    std::vector<orion::ExtensionId> ids;
    bool ids_done = false;
    orion::GetInstalledExtensionIdsQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    g_core_api->get_installed_extension_ids(q, [&](orion::GetAllInstalledExtensionIdsReply reply) {
        ids = reply.err ? std::vector<orion::ExtensionId>{} : reply.ids;
        ids_done = true;
    });
    while (!ids_done) orion::tls_run_loop()->pump();
    if (ids.empty()) return nullptr;

    // Step 2: fetch full extension data
    std::vector<orion::ExtensionDto> extensions;
    bool exts_done = false;
    orion::GetInstalledExtensionsQuery eq;
    eq.ids = ids;
    g_core_api->get_installed_extensions(eq, [&](orion::GetInstalledExtensionsReply reply) {
        extensions = reply.err ? std::vector<orion::ExtensionDto>{} : reply.extensions;
        exts_done = true;
    });
    while (!exts_done) orion::tls_run_loop()->pump();

    int count = (int)extensions.size();
    if (count == 0) return nullptr;

    // Single allocation: struct array followed by UTF-8 string data.
    size_t blob_size = count * sizeof(InstalledExtensionNative);
    for (int i = 0; i < count; i++) {
        blob_size += ((std::string)extensions[i].id).size() + 1;
        blob_size += extensions[i].name.size() + 1;
        blob_size += extensions[i].version.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<InstalledExtensionNative*>(base);
    char* str = base + count * sizeof(InstalledExtensionNative);

    auto copy_str = [&](const std::string& s) -> char* {
        char* dest = str;
        memcpy(str, s.c_str(), s.size() + 1);
        str += s.size() + 1;
        return dest;
    };

    for (int i = 0; i < count; i++) {
        arr[i].id         = copy_str((std::string)extensions[i].id);
        arr[i].name       = copy_str(extensions[i].name);
        arr[i].version    = copy_str(extensions[i].version);
        arr[i].type       = (int32_t)extensions[i].type;
        arr[i].is_enabled = extensions[i].is_enabled ? 1 : 0;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeInstalledExtensions(InstalledExtensionNative* extensions)
{
    free(extensions);
}

extern "C" __declspec(dllexport) ExtensionToolbarItemNative* GetExtensionToolbarItems(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    WebExtensions::WebExtensionsRuntime* runtime = readyRuntimeForProfile(profile_id);
    if (!runtime)
        return nullptr;

    auto ext_list = runtime->get_extensions();
    if (!ext_list)
        return nullptr;

    struct ActionInfo {
        std::string id;
        std::string title;
        std::string popup_uri;
        std::optional<WebExtensions::WebExtensionResourceData> icon;
    };
    std::vector<ActionInfo> actions;

    for (auto& weak_ext : *ext_list) {
        auto ext = weak_ext.lock();
        if (!ext)
            continue;
        auto popup = ext->default_popup_uri();
        if (!popup)
            continue;
        ActionInfo info;
        info.id = (std::string)ext->extension_id();
        info.title = ext->default_title().value_or(info.id);
        info.popup_uri = *popup;
        info.icon = ext->default_icon_data();
        actions.push_back(std::move(info));
    }

    if (actions.empty())
        return nullptr;

    size_t blob_size = actions.size() * sizeof(ExtensionToolbarItemNative);
    for (auto& a : actions) {
        blob_size += a.id.size() + 1;
        blob_size += a.title.size() + 1;
        blob_size += a.popup_uri.size() + 1;
        if (a.icon)
            blob_size += a.icon->datalen;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<ExtensionToolbarItemNative*>(base);
    char* str = base + actions.size() * sizeof(ExtensionToolbarItemNative);

    auto copy_str = [&](const std::string& s) -> char* {
        char* dest = str;
        memcpy(str, s.c_str(), s.size() + 1);
        str += s.size() + 1;
        return dest;
    };

    for (size_t i = 0; i < actions.size(); i++) {
        arr[i].id = copy_str(actions[i].id);
        arr[i].title = copy_str(actions[i].title);
        arr[i].popup_uri = copy_str(actions[i].popup_uri);
        if (actions[i].icon) {
            arr[i].icon_data = reinterpret_cast<uint8_t*>(str);
            memcpy(str, actions[i].icon->data, actions[i].icon->datalen);
            str += actions[i].icon->datalen;
            arr[i].icon_data_len = (int32_t)actions[i].icon->datalen;
        } else {
            arr[i].icon_data = nullptr;
            arr[i].icon_data_len = 0;
        }
    }

    *outCount = (int)actions.size();
    return arr;
}

extern "C" __declspec(dllexport) void FreeExtensionToolbarItems(ExtensionToolbarItemNative* items)
{
    free(items);
}

extern "C" __declspec(dllexport) void UninstallExtension(const char* extensionId)
{
    if (!g_core_api || !extensionId || !*extensionId) return;
    orion::UninstallExtensionQuery q;
    q.id = orion::ExtensionId(std::string(extensionId));
    bool done = false;
    g_core_api->uninstall_extension(q, [&](orion::UninstallExtensionReply) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void SetExtensionEnabled(const char* extensionId, int8_t enabled)
{
    if (!g_core_api || !extensionId || !*extensionId) return;
    orion::UpdateExtensionQuery q;
    q.id = orion::ExtensionId(std::string(extensionId));
    q.is_enabled = enabled != 0;
    q.update_is_enabled = true;
    bool done = false;
    g_core_api->update_extension(q, [&](orion::UpdateExtensionReply) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

// ---------------------------------------------------------------------------
// WebExtensions host wiring (bridge_hosts.h). The core owns the bridges; the
// facade attaches a ProfileHost per profile, a WebViewFactoryHost per
// activated world and a WebViewHost per browsing HWND, and forwards their
// native operations to the engine's ScriptBridge.
// ---------------------------------------------------------------------------

// Attaches the profile's host and activates its main world on first use.
// Returns the world's factory bridge, or null when extensions are unsupported
// (no script bridge) or the core has no world for the profile.
static std::shared_ptr<orion::bridge::WebViewFactoryBridge> activateProfileWorld(int64_t profile_id)
{
    if (!g_core_api || !g_script_bridge)
        return nullptr;
    const orion::ProfileId id(profile_id);
    if (g_profile_hosts.contains(profile_id)) {
        auto world = g_core_api->get_profile_world(id, orion::PrivateMode::no).lock();
        return world ? world->web_view_factory_bridge().lock() : nullptr;
    }
    // Process model: a Windows process hosts exactly one profile, and its
    // CoreApi was opened on that profile's own profile.db (see
    // docs_ai/profile_storage.md), so this map holds one entry. The core is
    // profile-agnostic and on Linux a single process attaches every profile
    // to the same CoreApi; do not bake either shape into the core. A second
    // id here would create a runtime that queries this profile's database for
    // another profile's extensions and silently finds none.
    if (!g_profile_hosts.empty()) {
        orion::log_warning("activateProfileWorld: refusing profile %lld; this process already hosts profile %lld",
            profile_id, (long long)g_profile_hosts.begin()->first);
        return nullptr;
    }

    ProfileExtensionHosts hosts;
    hosts.profile = std::make_shared<orion::win::ProfileHost>(*g_core_api, id);
    auto world = g_core_api->get_profile_world(id, orion::PrivateMode::no).lock();
    auto factory_bridge = world ? world->web_view_factory_bridge().lock() : nullptr;
    if (!factory_bridge) {
        orion::log_warning("activateProfileWorld: core has no main world for profile %lld", profile_id);
        return nullptr;
    }
    hosts.factory = std::make_unique<orion::win::WebViewFactoryHost>(*factory_bridge, *g_script_bridge, id);
    // Scheme handlers must exist before the first extension URL loads; the
    // engine keeps them for the process lifetime.
    factory_bridge->register_schemes();
    // Creates the runtime, calls initialize_platform(), starts the async
    // extension load. Idempotent while the runtime exists.
    world->initialize();
    // Registered right after initialize(): the reply fires when the initial
    // load finishes (runtime notify_update), not while packages still load.
    factory_bridge->when_ready([ready = hosts.extensions_ready] { *ready = true; });
    g_profile_hosts.emplace(profile_id, std::move(hosts));
    orion::log_debug("activateProfileWorld: profile %lld main world activated", profile_id);
    return factory_bridge;
}

// Pumps the run loop until the profile's runtime finished its initial
// extension load (DB query + package unpacking). Top-level exports only.
// False after 30 s or when the profile was never activated.
static bool waitForExtensionsReady(int64_t profile_id)
{
    const auto it = g_profile_hosts.find(profile_id);
    if (it == g_profile_hosts.end())
        return false;
    const std::shared_ptr<bool> ready = it->second.extensions_ready;  // pumping may mutate the map
    if (*ready)
        return true;
    orion::log_debug("waitForExtensionsReady: waiting for initial extension load (profile %lld)", profile_id);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while (!*ready) {
        orion::tls_run_loop()->pump();
        if (std::chrono::steady_clock::now() > deadline) {
            orion::log_warning("waitForExtensionsReady: extension runtime for profile %lld not ready after 30 s", profile_id);
            return false;
        }
    }
    return true;
}

// The profile's extension runtime once ready; null when extensions are
// unsupported (no script bridge) or the runtime never became ready.
static WebExtensions::WebExtensionsRuntime* readyRuntimeForProfile(int64_t profile_id)
{
    auto factory = activateProfileWorld(profile_id);
    if (!factory || !waitForExtensionsReady(profile_id))
        return nullptr;
    auto world = factory->parent_world().lock();
    return world ? world->runtime() : nullptr;
}

// Views first (detaching drops their bridges), then each world's runtime,
// then the profile anchors (BUILDING.md, "Attachment lifetime and shutdown").
static void shutdownExtensionHosts()
{
    g_web_view_hosts.clear();
    for (auto& [profile_id, hosts] : g_profile_hosts) {
        if (g_core_api) {
            if (auto world = g_core_api->get_profile_world(
                    orion::ProfileId(profile_id), orion::PrivateMode::no).lock())
                world->cleanup();
        }
        hosts.factory.reset();
        hosts.profile.reset();
    }
    g_profile_hosts.clear();
}

// Wires a browsing view into the profile's extension runtime: attaches a
// WebViewHost for the HWND and lets the core inject the bridge scripts and
// message handlers. Called by C# right after CreateOrionWindow.
extern "C" __declspec(dllexport) void ConfigureWebViewExtensions(HWND hwnd, int64_t profile_id)
{
    if (!g_core_api || profile_id == 0 || !hwnd) {
        orion::log_warning("ConfigureWebViewExtensions: skipped (core=%d, profileId=%lld, hwnd=%p)",
            g_core_api ? 1 : 0, profile_id, hwnd);
        return;
    }
    if (!g_script_bridge) {
        orion::log_debug("ConfigureWebViewExtensions: engine has no script bridge; extensions disabled");
        return;
    }
    auto factory = activateProfileWorld(profile_id);
    if (!factory) {
        orion::log_warning("ConfigureWebViewExtensions: no factory bridge for profile %lld", profile_id);
        return;
    }

    // Configuring a view before the runtime's initial extension load would
    // inject no bridge scripts and the page would never see browser.*. GTK
    // defers view creation until ready; here the view already exists, so
    // wait (pumping the run loop) for the world.
    if (!waitForExtensionsReady(profile_id)) {
        orion::log_warning("ConfigureWebViewExtensions: view hwnd=%p left without extensions", hwnd);
        return;
    }

    // Re-configuring an HWND replaces its host (a fresh bridge per view).
    g_web_view_hosts.erase(hwnd);
    auto view_bridge = factory->create_web_view_bridge().lock();
    if (!view_bridge) {
        orion::log_warning("ConfigureWebViewExtensions: could not create a view bridge (profile %lld, hwnd=%p)", profile_id, hwnd);
        return;
    }
    auto host = std::make_unique<orion::win::WebViewHost>(*view_bridge, *g_script_bridge, hwnd);
    // After attachment (scripts and handlers must reach the host), before the
    // page loads extension content.
    factory->configure_web_view(*view_bridge);
    g_web_view_hosts[hwnd] = std::move(host);
    orion::log_debug("ConfigureWebViewExtensions: hwnd=%p profileId=%lld", hwnd, profile_id);
}

// -----------------------------------------------------------------------
// Windows (tab groups / saved windows)
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) WindowNative* GetWindows(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    std::vector<orion::WindowDto> windows;
    bool done = false;
    g_core_api->get_windows_by_query(orion::ProfileId(profile_id), [&](orion::GetWindowsReply reply) {
        windows = reply.err ? std::vector<orion::WindowDto>{} : reply.windows;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)windows.size();
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(WindowNative);
    for (int i = 0; i < count; i++)
        blob_size += windows[i].name.size() + 1 + windows[i].keyboard_shortcut.size() + 1;

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<WindowNative*>(base);
    char* str = base + count * sizeof(WindowNative);

    for (int i = 0; i < count; i++) {
        arr[i].id              = (int64_t)windows[i].id;
        arr[i].profile_id      = (int64_t)windows[i].profile_id;
        arr[i].is_tab_group    = windows[i].is_tab_group ? 1 : 0;
        arr[i].selected_tab_id = (int64_t)windows[i].selected_tab_id;
        arr[i].has_ui_window   = windows[i].has_ui_window ? 1 : 0;
        arr[i].is_private      = windows[i].is_private ? 1 : 0;

        arr[i].name = str;
        memcpy(str, windows[i].name.c_str(), windows[i].name.size() + 1);
        str += windows[i].name.size() + 1;

        arr[i].keyboard_shortcut = str;
        memcpy(str, windows[i].keyboard_shortcut.c_str(), windows[i].keyboard_shortcut.size() + 1);
        str += windows[i].keyboard_shortcut.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeWindows(WindowNative* windows)
{
    free(windows);
}

extern "C" __declspec(dllexport) int64_t CreateOrionWindowEntry(
    int64_t profile_id, int8_t is_tab_group, int8_t has_ui_window)
{
    if (!g_core_api || profile_id == 0) return 0;

    orion::CreateWindowQuery q;
    q.profile_id    = orion::ProfileId(profile_id);
    q.is_tab_group  = is_tab_group != 0;
    q.has_ui_window = has_ui_window != 0;

    int64_t result = 0;
    bool done = false;
    g_core_api->create_window(q, [&](orion::CreateWindowReply reply) {
        result = reply.err ? 0LL : (int64_t)reply.id;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result;
}

extern "C" __declspec(dllexport) void UpdateWindowSelectedTab(int64_t window_id, int64_t selected_tab_id)
{
    if (!g_core_api) return;
    orion::UpdateWindowQuery q;
    q.id                    = orion::WindowId(window_id);
    q.selected_tab_id       = orion::TabId(selected_tab_id);
    q.update_selected_tab_id = true;
    g_core_api->update_window(q, [](orion::Error) {});
}

extern "C" __declspec(dllexport) void UpdateWindowHasUiWindow(int64_t window_id, int8_t has_ui_window)
{
    if (!g_core_api) return;
    orion::UpdateWindowQuery q;
    q.id                  = orion::WindowId(window_id);
    q.has_ui_window       = has_ui_window != 0;
    q.update_has_ui_window = true;
    bool done = false;
    g_core_api->update_window(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void UpdateWindowIsTabGroup(int64_t window_id, int8_t is_tab_group)
{
    if (!g_core_api) return;
    orion::UpdateWindowQuery q;
    q.id                   = orion::WindowId(window_id);
    q.is_tab_group         = is_tab_group != 0;
    q.update_is_tab_group  = true;
    bool done = false;
    g_core_api->update_window(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void UpdateWindowName(int64_t window_id, const char* name)
{
    if (!g_core_api) return;
    orion::UpdateWindowQuery q;
    q.id          = orion::WindowId(window_id);
    q.name        = name;
    q.update_name = true;
    bool done = false;
    g_core_api->update_window(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void UpdateWindowKeyboardShortcut(int64_t window_id, const char* shortcut)
{
    if (!g_core_api) return;
    orion::UpdateWindowQuery q;
    q.id                       = orion::WindowId(window_id);
    q.keyboard_shortcut        = shortcut;
    q.update_keyboard_shortcut = true;
    bool done = false;
    g_core_api->update_window(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void MoveWindowEntry(int64_t window_id, int64_t next_window_id)
{
    if (!g_core_api) return;
    orion::MoveWindowQuery q;
    q.window_id       = orion::WindowId(window_id);
    q.next_window_id = orion::WindowId(next_window_id);
    bool done = false;
    g_core_api->move_window(q, [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void CloseOrionWindowEntry(int64_t window_id)
{
    if (!g_core_api) return;
    bool done = false;
    g_core_api->close_window(orion::WindowId(window_id), [&](orion::Error) { done = true; });
    while (!done) orion::tls_run_loop()->pump();
}

// -----------------------------------------------------------------------
// Tabs
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) TabNative* GetTabs(int64_t window_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || window_id == 0)
        return nullptr;

    std::vector<orion::TabDto> tabs;
    bool done = false;
    orion::GetTabIdsQuery q;
    q.window_id = orion::WindowId(window_id);
    g_core_api->get_tabs_by_query(q, [&](orion::GetTabsReply reply) {
        tabs = reply.err ? std::vector<orion::TabDto>{} : reply.tabs;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)tabs.size();
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(TabNative);
    for (int i = 0; i < count; i++) {
        blob_size += tabs[i].name.size() + 1;
        blob_size += tabs[i].uri.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<TabNative*>(base);
    char* str = base + count * sizeof(TabNative);

    for (int i = 0; i < count; i++) {
        arr[i].id            = (int64_t)tabs[i].id;
        arr[i].window_id     = (int64_t)tabs[i].window_id;
        arr[i].is_pinned     = tabs[i].is_pinned ? 1 : 0;
        arr[i].parent_tab_id = (int64_t)tabs[i].parent_tab_id;
        arr[i].is_collapsed  = tabs[i].is_collapsed ? 1 : 0;

        arr[i].name = str;
        memcpy(str, tabs[i].name.c_str(), tabs[i].name.size() + 1);
        str += tabs[i].name.size() + 1;

        arr[i].uri = str;
        memcpy(str, tabs[i].uri.c_str(), tabs[i].uri.size() + 1);
        str += tabs[i].uri.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeTabs(TabNative* tabs)
{
    free(tabs);
}

extern "C" __declspec(dllexport) int64_t CreateTab(
    int64_t window_id, const char* uri, const char* name,
    int8_t is_pinned, int64_t parent_tab_id)
{
    if (!g_core_api || window_id == 0) return 0;

    orion::CreateTabQuery q;
    q.window_id     = orion::WindowId(window_id);
    q.uri           = uri ? uri : "";
    q.name          = name ? name : "";
    q.is_pinned     = is_pinned != 0;
    q.parent_tab_id = orion::TabId(parent_tab_id);

    int64_t result = 0;
    bool done = false;
    g_core_api->create_tab(q, [&](orion::CreateTabReply reply) {
        result = reply.err ? 0LL : (int64_t)reply.id;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result;
}

extern "C" __declspec(dllexport) void UpdateTabUri(int64_t tab_id, const char* uri)
{
    if (!g_core_api || tab_id == 0) return;
    orion::UpdateTabQuery q;
    q.id         = orion::TabId(tab_id);
    q.uri        = uri ? uri : "";
    q.update_uri = true;
    g_core_api->update_tab(q, [](orion::Error) {});
}

extern "C" __declspec(dllexport) void UpdateTabName(int64_t tab_id, const char* name)
{
    if (!g_core_api || tab_id == 0) return;
    orion::UpdateTabQuery q;
    q.id          = orion::TabId(tab_id);
    q.name        = name ? name : "";
    q.update_name = true;
    g_core_api->update_tab(q, [](orion::Error) {});
}

extern "C" __declspec(dllexport) int64_t GetSharedPinnedTabsWindowId(int64_t profile_id)
{
    if (!g_core_api || profile_id == 0) return 0;
    orion::WindowId window_id{};
    bool done = false;
    g_core_api->get_shared_pinned_tabs_window_id(orion::ProfileId(profile_id),
        [&](orion::GetSharedPinnedTabsWindowIdReply reply) {
            if (!reply.err) window_id = reply.window_id;
            done = true;
        });
    while (!done) orion::tls_run_loop()->pump();
    return window_id.value;
}

extern "C" __declspec(dllexport) void UpdateTabPinned(int64_t tab_id, int64_t window_id, int8_t is_pinned)
{
    if (!g_core_api || tab_id == 0 || window_id == 0) return;
    orion::UpdateTabQuery q;
    q.id               = orion::TabId(tab_id);
    q.window_id        = orion::WindowId(window_id);
    q.is_pinned        = is_pinned != 0;
    q.update_is_pinned = true;
    g_core_api->update_tab(q, [](orion::Error) {});
}

extern "C" __declspec(dllexport) void UpdateTabWindowId(int64_t tab_id, int64_t window_id)
{
    if (!g_core_api || tab_id == 0) return;
    orion::UpdateTabQuery q;
    q.id               = orion::TabId(tab_id);
    q.window_id        = orion::WindowId(window_id);
    q.update_window_id = true;
    g_core_api->update_tab(q, [](orion::Error) {});
}

extern "C" __declspec(dllexport) void MoveTab(
    int64_t tab_id, int64_t target_window_id, int64_t parent_tab_id,
    int64_t after_tab_id, int8_t is_nested)
{
    if (!g_core_api || tab_id == 0) return;
    orion::MoveTabQuery q;
    q.tab_id                  = orion::TabId(tab_id);
    q.target_window_id        = orion::WindowId(target_window_id);
    q.parent_tab_id           = orion::TabId(parent_tab_id);
    q.after_tab_id            = orion::TabId(after_tab_id);
    q.is_nested_tabs_operation = is_nested != 0;
    bool done = false;
    g_core_api->move_tab(q, [&](orion::Error err) {
        if (err)
            orion::log_error("MoveTab: %s", err.message().c_str());
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void DeleteTabs(const int64_t* ids, int count)
{
    if (!g_core_api || count <= 0) return;
    std::vector<orion::TabId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::TabId(ids[i]));
    g_core_api->delete_tabs(id_vec, [](orion::Error) {});
}

// -----------------------------------------------------------------------
// Recently Closed Tabs
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) void CreateRecentlyClosedTab(
    int64_t profile_id, const char* name, const char* uri)
{
    if (!g_core_api || profile_id == 0) return;
    orion::CreateRecentlyClosedTabQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.name       = name ? name : "";
    q.uri        = uri ? uri : "";
    q.closed_at  = orion::UnixTimeUtc64(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
    g_core_api->create_recently_closed_tab(q, [](orion::CreateRecentlyClosedTabReply) {});
}

extern "C" __declspec(dllexport) RecentlyClosedTabNative* GetRecentlyClosedTabs(
    int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    // Step 1: fetch IDs
    std::vector<orion::RecentlyClosedTabId> ids;
    bool ids_done = false;
    orion::GetRecentlyClosedTabIdsQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    g_core_api->get_recently_closed_tab_ids(q, [&](orion::GetRecentlyClosedTabIdsReply reply) {
        ids = reply.err ? std::vector<orion::RecentlyClosedTabId>{} : reply.ids;
        ids_done = true;
    });
    while (!ids_done) orion::tls_run_loop()->pump();
    if (ids.empty()) return nullptr;

    // Step 2: fetch full entries
    std::vector<orion::RecentlyClosedTabDto> entries;
    bool done = false;
    g_core_api->get_recently_closed_tabs(ids, [&](orion::GetRecentlyClosedTabsReply reply) {
        entries = reply.err ? std::vector<orion::RecentlyClosedTabDto>{} : reply.tabs;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)entries.size();
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(RecentlyClosedTabNative);
    for (int i = 0; i < count; i++) {
        blob_size += entries[i].name.size() + 1;
        blob_size += entries[i].uri.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<RecentlyClosedTabNative*>(base);
    char* str = base + count * sizeof(RecentlyClosedTabNative);

    for (int i = 0; i < count; i++) {
        arr[i].id         = (int64_t)entries[i].id;
        arr[i].profile_id = (int64_t)entries[i].profile_id;
        arr[i].closed_at  = (int64_t)entries[i].closed_at;
        arr[i].window_id  = entries[i].window_id.has_value() ? (int64_t)*entries[i].window_id : 0;

        arr[i].name = str;
        memcpy(str, entries[i].name.c_str(), entries[i].name.size() + 1);
        str += entries[i].name.size() + 1;

        arr[i].uri = str;
        memcpy(str, entries[i].uri.c_str(), entries[i].uri.size() + 1);
        str += entries[i].uri.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeRecentlyClosedTabs(RecentlyClosedTabNative* tabs)
{
    free(tabs);
}

extern "C" __declspec(dllexport) void DeleteRecentlyClosedTabs(const int64_t* ids, int count)
{
    if (!g_core_api || count <= 0) return;
    orion::DeleteRecentlyClosedTabsQuery q;
    q.ids.reserve(count);
    for (int i = 0; i < count; i++)
        q.ids.push_back(orion::RecentlyClosedTabId(ids[i]));
    g_core_api->delete_recently_closed_tabs(q, [](orion::Error) {});
}

// -----------------------------------------------------------------------
// Recently Closed Windows
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) RecentlyClosedWindowNative* GetRecentlyClosedWindows(
    int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0)
        return nullptr;

    // Step 1: fetch IDs
    std::vector<orion::RecentlyClosedWindowId> ids;
    bool ids_done = false;
    g_core_api->get_recently_closed_window_ids(orion::ProfileId(profile_id),
        [&](orion::GetRecentlyClosedWindowIdsReply reply) {
            ids = reply.err ? std::vector<orion::RecentlyClosedWindowId>{} : reply.ids;
            ids_done = true;
        });
    while (!ids_done) orion::tls_run_loop()->pump();
    if (ids.empty()) return nullptr;

    // Step 2: fetch full entries
    std::vector<orion::RecentlyClosedWindowDto> entries;
    bool done = false;
    g_core_api->get_recently_closed_windows(ids, [&](orion::GetRecentlyClosedWindowsReply reply) {
        entries = reply.err ? std::vector<orion::RecentlyClosedWindowDto>{} : reply.windows;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = (int)entries.size();
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(RecentlyClosedWindowNative);
    for (int i = 0; i < count; i++)
        blob_size += entries[i].name.size() + 1;

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<RecentlyClosedWindowNative*>(base);
    char* str = base + count * sizeof(RecentlyClosedWindowNative);

    for (int i = 0; i < count; i++) {
        arr[i].id         = (int64_t)entries[i].id;
        arr[i].profile_id = (int64_t)entries[i].profile_id;
        arr[i].closed_at  = (int64_t)entries[i].closed_at;

        arr[i].name = str;
        memcpy(str, entries[i].name.c_str(), entries[i].name.size() + 1);
        str += entries[i].name.size() + 1;
    }
    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeRecentlyClosedWindows(RecentlyClosedWindowNative* windows)
{
    free(windows);
}

extern "C" __declspec(dllexport) void DeleteRecentlyClosedWindows(const int64_t* ids, int count)
{
    if (!g_core_api || count <= 0) return;
    std::vector<orion::RecentlyClosedWindowId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::RecentlyClosedWindowId(ids[i]));
    g_core_api->delete_recently_closed_windows(id_vec, [](orion::Error) {});
}

// -----------------------------------------------------------------------
// Undo/Redo Tabs
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) void CreateUndoRedoTabStep(
    int64_t window_id, const int64_t* tab_ids, int count)
{
    if (!g_core_api || window_id == 0 || count <= 0) return;

    orion::CreateUndoRedoTabStepQuery q;
    q.window_id = orion::WindowId(window_id);
    q.tab_ids.reserve(count);
    for (int i = 0; i < count; i++)
        q.tab_ids.push_back(orion::TabId(tab_ids[i]));

    bool done = false;
    g_core_api->create_undo_redo_tab_step(q, [&](orion::CreateUndoRedoTabStepReply reply) {
        if (reply.err)
            orion::log_error("CreateUndoRedoTabStep: %s", reply.err.message().c_str());
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void ChangeUndoRedoTabStep(
    int64_t window_id, int8_t is_undo)
{
    if (!g_core_api || window_id == 0) return;

    orion::ChangeUndoRedoTabStepQuery q;
    q.window_id = orion::WindowId(window_id);
    q.is_undo   = is_undo != 0;

    bool done = false;
    g_core_api->change_undo_redo_tab_step(q, [&](orion::ChangeUndoRedoTabStepReply reply) {
        if (reply.err)
            orion::log_error("ChangeUndoRedoTabStep: %s", reply.err.message().c_str());
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void IsUndoRedoTabStepAvailable(
    int64_t window_id, int8_t* out_undo, int8_t* out_redo)
{
    *out_undo = 0;
    *out_redo = 0;
    if (!g_core_api || window_id == 0) return;

    orion::IsUndoRedoTabStepAvailableQuery q;
    q.window_id = orion::WindowId(window_id);

    bool done = false;
    g_core_api->is_undo_redo_tab_step_available(q, [&](orion::IsUndoRedoTabStepAvailableReply reply) {
        if (!reply.err) {
            *out_undo = reply.is_undo_available ? 1 : 0;
            *out_redo = reply.is_redo_available ? 1 : 0;
        }
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

// -----------------------------------------------------------------------
// Logging
// -----------------------------------------------------------------------

namespace {

// Reads from the pipe fed by the redirected stdout/stderr and forwards the
// output, line by line, to OutputDebugStringW. Runs until all write ends of
// the pipe close (i.e. process teardown), then exits.
void PumpStdStreamPipe(HANDLE read_pipe)
{
    std::string pending;
    char buf[4096];
    DWORD read = 0;
    while (ReadFile(read_pipe, buf, sizeof(buf), &read, nullptr) && read > 0) {
        pending.append(buf, read);

        // Emit complete lines so the debugger output stays line-oriented.
        size_t nl;
        while ((nl = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, nl);
            pending.erase(0, nl + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            line.push_back('\n');
            OutputDebugStringW(widen(line).c_str());
        }

        // Guard against a producer that never emits a newline.
        if (pending.size() > 64 * 1024) {
            OutputDebugStringW(widen(pending).c_str());
            pending.clear();
        }
    }
    if (!pending.empty())
        OutputDebugStringW(widen(pending).c_str());
}

// Redirects stdout/stderr into a pipe whose contents are forwarded to
// OutputDebugStringW, so stray printf / fwrite(stderr) / std::cout output and
// raw WriteFile-on-std-handle output from third-party libraries show up in the
// debugger. Captures both the CRT streams (fd 1/2) and the Win32 std handles.
void RedirectStdStreamsToDebugOutput()
{
    static bool installed = false;
    if (installed) return;
    installed = true;

    HANDLE read_pipe = nullptr, write_pipe = nullptr;
    if (!CreatePipe(&read_pipe, &write_pipe, nullptr, 64 * 1024))
        return;

    // Don't let CEF subprocesses inherit either end of the pipe.
    SetHandleInformation(read_pipe, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(write_pipe, HANDLE_FLAG_INHERIT, 0);

    // OS-level writes: WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), ...).
    SetStdHandle(STD_OUTPUT_HANDLE, write_pipe);
    SetStdHandle(STD_ERROR_HANDLE, write_pipe);

    // CRT-level writes: printf, fwrite(stderr), std::cout/cerr (fd 1/2).
    // _open_osfhandle takes ownership of the handle it is given, so hand it a
    // duplicate — otherwise closing its fd would close the handle still held by
    // SetStdHandle above.
    HANDLE crt_handle = nullptr;
    if (DuplicateHandle(GetCurrentProcess(), write_pipe,
                        GetCurrentProcess(), &crt_handle,
                        0, FALSE, DUPLICATE_SAME_ACCESS)) {
        int fd = _open_osfhandle(reinterpret_cast<intptr_t>(crt_handle), _O_WRONLY | _O_BINARY);
        if (fd != -1) {
            _dup2(fd, _fileno(stdout));
            _dup2(fd, _fileno(stderr));
            _close(fd); // stdout/stderr keep their own dup'd handles
            setvbuf(stdout, nullptr, _IONBF, 0);
            setvbuf(stderr, nullptr, _IONBF, 0);
        } else {
            CloseHandle(crt_handle);
        }
    }

    std::thread(PumpStdStreamPipe, read_pipe).detach();
}

} // namespace

extern "C" __declspec(dllexport) void SetupLogging()
{
#ifdef _DEBUG
    RedirectStdStreamsToDebugOutput();
#endif

    orion::set_log_sink([](const char* line) {
        OutputDebugStringA(line);
    });
}

extern "C" __declspec(dllexport) void OrionLog(char level, const char* message)
{
    orion::log_message(level, message);
}

// -----------------------------------------------------------------------
// Favicons
// -----------------------------------------------------------------------

extern "C" __declspec(dllexport) void SetFavicon(
    const char* page_url, const char* icon_url,
    const uint8_t* data, int data_size)
{
    if (!g_core_api || !page_url || !*page_url || !icon_url || !*icon_url)
        return;

    orion::SetFaviconQuery q;
    q.page_url = page_url;
    q.icon_url = icon_url;
    if (data && data_size > 0)
        q.data.assign(data, data + data_size);

    g_core_api->set_favicon(q, [](orion::Error) {});
}

extern "C" __declspec(dllexport) uint8_t* GetFavicon(
    const char* page_url, int* out_size, int8_t* out_expired,
    wchar_t** out_icon_url)
{
    *out_size = 0;
    *out_expired = 0;
    *out_icon_url = nullptr;
    if (!g_core_api || !page_url || !*page_url)
        return nullptr;

    orion::GetFaviconReply reply;
    bool done = false;
    g_core_api->get_favicon(page_url, [&](orion::GetFaviconReply r) {
        reply = std::move(r);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    if (reply.err || !reply.found || reply.data.empty())
        return nullptr;

    *out_expired = reply.expired ? 1 : 0;
    if (!reply.icon_url.empty())
        *out_icon_url = ReturnWString(widen(reply.icon_url));

    auto* buf = static_cast<uint8_t*>(malloc(reply.data.size()));
    if (!buf) return nullptr;
    memcpy(buf, reply.data.data(), reply.data.size());
    *out_size = (int)reply.data.size();
    return buf;
}

extern "C" __declspec(dllexport) void FreeFavicon(uint8_t* data)
{
    free(data);
}

// ---------------------------------------------------------------------------
// Passwords (via CoreApi)
// ---------------------------------------------------------------------------

struct PasswordItemNative {
    int64_t id;
    int64_t profile_id;
    char* url;
    char* username;
    char* password;
    char* verification_key;
    char* verification_code;
};

static PasswordItemNative*
PackPasswordItems(std::vector<orion::PasswordItemDto>& items, int* out_count)
{
    int count = static_cast<int>(items.size());
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(PasswordItemNative);
    for (int i = 0; i < count; i++) {
        blob_size += items[i].url.size() + 1;
        blob_size += items[i].username.size() + 1;
        blob_size += items[i].password.size() + 1;
        blob_size += items[i].verification_key.size() + 1;
        blob_size += items[i].verification_code.size() + 1;
    }

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<PasswordItemNative*>(base);
    char* str = base + count * sizeof(PasswordItemNative);

    for (int i = 0; i < count; i++) {
        arr[i].id = static_cast<int64_t>(items[i].id);
        arr[i].profile_id = static_cast<int64_t>(items[i].profile_id);

        auto copy_str = [&](const std::string& src) -> char* {
            char* dst = str;
            memcpy(str, src.c_str(), src.size() + 1);
            str += src.size() + 1;
            return dst;
        };
        arr[i].url = copy_str(items[i].url);
        arr[i].username = copy_str(items[i].username);
        arr[i].password = copy_str(items[i].password);
        arr[i].verification_key = copy_str(items[i].verification_key);
        arr[i].verification_code = copy_str(items[i].verification_code);
    }
    for (auto& dto : items)
        SecureZeroMemory(dto.password.data(), dto.password.size());

    *out_count = count;
    return arr;
}

extern "C" __declspec(dllexport) int64_t*
GetPasswordItemIds(int64_t profile_id, const char* search_text, const char* authority, int* out_count)
{
    *out_count = 0;
    if (!g_core_api || profile_id == 0) return nullptr;

    orion::GetPasswordItemIdsQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.search_text = search_text ? search_text : "";
    q.authority = authority ? authority : "";

    std::vector<orion::PasswordItemId> ids;
    bool done = false;
    g_core_api->get_password_item_ids(q, [&](orion::GetPasswordItemIdsReply reply) {
        if (!reply.err)
            ids = std::move(reply.ids);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = static_cast<int>(ids.size());
    if (count == 0) return nullptr;

    auto* arr = static_cast<int64_t*>(malloc(count * sizeof(int64_t)));
    for (int i = 0; i < count; i++)
        arr[i] = static_cast<int64_t>(ids[i]);
    *out_count = count;
    return arr;
}

extern "C" __declspec(dllexport) void
FreePasswordItemIds(int64_t* ids)
{
    free(ids);
}

extern "C" __declspec(dllexport) PasswordItemNative*
GetPasswordItemsByIds(const int64_t* ids, int count, int* out_count)
{
    *out_count = 0;
    if (!g_core_api || count == 0) return nullptr;

    std::vector<orion::PasswordItemId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::PasswordItemId(ids[i]));

    std::vector<orion::PasswordItemDto> items;
    bool done = false;
    g_core_api->get_password_items(id_vec, [&](orion::GetPasswordItemsReply reply) {
        if (!reply.err)
            items = std::move(reply.items);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    return PackPasswordItems(items, out_count);
}

extern "C" __declspec(dllexport) const char*
ExportPasswordItemsCsv(const int64_t* ids, int count)
{
    if (!g_core_api || count == 0) return ReturnString("");

    std::vector<orion::PasswordItemId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::PasswordItemId(ids[i]));

    std::string csv;
    bool done = false;
    g_core_api->get_password_items_csv_export(id_vec, [&](orion::GetPasswordItemsCsvExportReply reply) {
        if (!reply.err)
            csv = std::move(reply.csv_text);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    return ReturnString(csv);
}

extern "C" __declspec(dllexport) int
ImportPasswordItemsFromCsv(int64_t profile_id, const char* csv_text,
                           int* out_imported_count, int* out_not_imported_count)
{
    if (!g_core_api || profile_id == 0 || !csv_text) return 0;

    orion::ImportPasswordItemsFromCsvQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.csv_text = csv_text;

    bool success = false;
    bool done = false;
    g_core_api->import_password_items_from_csv(q, [&](orion::ImportPasswordItemsFromCsvReply reply) {
        if (!reply.err) {
            success = true;
            if (out_imported_count)
                *out_imported_count = static_cast<int>(reply.imported.size());
            if (out_not_imported_count)
                *out_not_imported_count = static_cast<int>(reply.not_imported.size());
        }
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    return success ? 1 : 0;
}

extern "C" __declspec(dllexport) int64_t
CreatePasswordItem(int64_t profile_id, const char* url, const char* username,
                   const char* password, const char* verification_key,
                   const char* verification_code, bool update_existing)
{
    if (!g_core_api || profile_id == 0) return 0;

    orion::CreatePasswordItemQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.url = url ? url : "";
    q.username = username ? username : "";
    q.password = password ? password : "";
    q.verification_key = verification_key ? verification_key : "";
    q.verification_code = verification_code ? verification_code : "";
    q.update_existing = update_existing;

    int64_t result_id = 0;
    bool done = false;
    g_core_api->create_password_item(q, [&](orion::CreatePasswordItemReply reply) {
        if (!reply.err)
            result_id = static_cast<int64_t>(reply.id);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    SecureZeroMemory(q.password.data(), q.password.size());
    return result_id;
}

extern "C" __declspec(dllexport) void
UpdatePasswordItem(int64_t id, const char* url, const char* username,
                   const char* password, const char* verification_key,
                   const char* verification_code)
{
    if (!g_core_api || id == 0) return;

    orion::UpdatePasswordItemQuery q;
    q.id = orion::PasswordItemId(id);
    q.url = url ? url : "";
    q.username = username ? username : "";
    q.password = password ? password : "";
    q.verification_key = verification_key ? verification_key : "";
    q.verification_code = verification_code ? verification_code : "";
    q.update_url = true;
    q.update_username = true;
    q.update_password = true;
    q.update_verification_key = true;
    q.update_verification_code = true;

    bool done = false;
    g_core_api->update_password_item(q, [&](orion::Error) {
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    SecureZeroMemory(q.password.data(), q.password.size());
}

extern "C" __declspec(dllexport) void
DeletePasswordItems(const int64_t* ids, int count)
{
    if (!g_core_api || count == 0) return;

    std::vector<orion::PasswordItemId> id_vec;
    id_vec.reserve(count);
    for (int i = 0; i < count; i++)
        id_vec.push_back(orion::PasswordItemId(ids[i]));

    bool done = false;
    g_core_api->delete_password_items(id_vec, [&](orion::Error) {
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
}

extern "C" __declspec(dllexport) void
FreePasswordItems(PasswordItemNative* items, int count)
{
    if (!items) return;
    for (int i = 0; i < count; i++) {
        if (items[i].password)
            SecureZeroMemory(items[i].password, strlen(items[i].password));
    }
    free(items);
}

extern "C" __declspec(dllexport) int64_t
CreateNeverSavePasswordItem(int64_t profile_id, const char* url)
{
    if (!g_core_api || profile_id == 0) return 0;

    orion::CreateNeverSavePasswordItemQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.url = url ? url : "";

    int64_t result_id = 0;
    bool done = false;
    g_core_api->create_never_save_password_item(q, [&](orion::CreateNeverSavePasswordItemReply reply) {
        if (!reply.err)
            result_id = static_cast<int64_t>(reply.id);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    return result_id;
}

extern "C" __declspec(dllexport) bool
GetNeverSavePasswordItem(int64_t profile_id, const char* authority)
{
    if (!g_core_api || profile_id == 0) return false;

    orion::GetNeverSavePasswordItemQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.authority = authority ? authority : "";

    bool never_save = false;
    bool done = false;
    g_core_api->get_never_save_password_item(q, [&](orion::GetNeverSavePasswordItemReply reply) {
        if (!reply.err)
            never_save = reply.never_save;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    return never_save;
}

// ── Snapshots ──────────────────────────────────────────────────────

extern "C" __declspec(dllexport) void CreateSnapshot(int64_t profile_id)
{
    if (!g_core_api || profile_id == 0) return;
    g_core_api->create_snapshot(orion::ProfileId(profile_id), [](orion::CreateSnapshotReply) {});
}

extern "C" __declspec(dllexport) int64_t GetLastSnapshotTimestamp(int64_t profile_id)
{
    if (!g_core_api || profile_id == 0) return 0;
    int64_t result = 0;
    bool done = false;
    g_core_api->get_last_snapshot(orion::ProfileId(profile_id), [&](orion::GetLastSnapshotReply reply) {
        if (!reply.err)
            result = reply.snapshot.created_at.value;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result;
}

extern "C" __declspec(dllexport) int64_t RestoreSnapshot(int64_t profile_id, int64_t created_at)
{
    if (!g_core_api || profile_id == 0 || created_at == 0) return 0;
    orion::RestoreSnapshotQuery query;
    query.profile_id = orion::ProfileId(profile_id);
    query.created_at = orion::UnixTimeUtc64(created_at);
    query.create_snapshot_before_restore = true;
    int64_t result_timestamp = 0;
    bool done = false;
    g_core_api->restore_snapshot(query, [&](orion::RestoreSnapshotReply reply) {
        if (!reply.err)
            result_timestamp = reply.created_at.value;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result_timestamp;
}

typedef void(__cdecl* SyncProgressCallback)(int done, int total, const wchar_t* description);
typedef void(__cdecl* SyncCompletionCallback)(int success, const wchar_t* errorMessage);

// sync_service.cpp prepends "file://" to the directory, so Windows paths like
// "C:\Users\foo" must become "/C:/Users/foo" to form a valid file URI.
static std::string toUriPath(const wchar_t* windowsPath)
{
    std::string path = narrow(windowsPath);
    std::replace(path.begin(), path.end(), '\\', '/');
    if (!path.empty() && path[0] != '/')
        path.insert(path.begin(), '/');
    return path;
}

extern "C" __declspec(dllexport) void ExportProfileData(
    int64_t profile_id, const wchar_t* directory,
    SyncProgressCallback progressCb, SyncCompletionCallback completionCb)
{
    if (!g_core_api || !directory || !completionCb)
        return;

    g_sync_service = g_core_api->make_sync_service();
    std::string dir = toUriPath(directory);

    try {
        g_sync_handle = g_sync_service->export_to(
            orion::ProfileId(profile_id), dir,
            [progressCb](int done, int total, const std::string& description) {
                if (progressCb) {
                    std::wstring wdesc = widen(description);
                    progressCb(done, total, wdesc.c_str());
                }
            },
            [completionCb](orion::Error result) {
                if (result) {
                    std::wstring msg = widen(result.message());
                    completionCb(0, msg.c_str());
                } else {
                    completionCb(1, nullptr);
                }
                g_sync_handle.reset();
                g_sync_service.reset();
            });
    } catch (const std::exception& e) {
    }
}

extern "C" __declspec(dllexport) void ImportProfileData(
    int64_t profile_id, const wchar_t* source,
    SyncProgressCallback progressCb, SyncCompletionCallback completionCb)
{
    if (!g_core_api || !source || !completionCb)
        return;

    g_sync_service = g_core_api->make_sync_service();
    std::string src = toUriPath(source);

    try {
        g_sync_handle = g_sync_service->import_from(
            orion::ProfileId(profile_id), src,
            [progressCb](int done, int total, const std::string& description) {
                if (progressCb) {
                    std::wstring wdesc = widen(description);
                    progressCb(done, total, wdesc.c_str());
                }
            },
            [completionCb](orion::Error result) {
                if (result) {
                    std::wstring msg = widen(result.message());
                    completionCb(0, msg.c_str());
                } else {
                    completionCb(1, nullptr);
                }
                g_sync_handle.reset();
                g_sync_service.reset();
            });
    } catch (const std::exception& e) {
    }
}

extern "C" __declspec(dllexport) void CancelSync()
{
    if (g_sync_handle)
        g_sync_handle->cancel();
}

// ─────────────────────────────────────────────────────────────────────────────
//  Downloads
// ─────────────────────────────────────────────────────────────────────────────

static void fillDownloadNative(DownloadEntryNative& out, const orion::DownloadDto& dto, char*& str)
{
    out.id              = dto.id.value;
    out.profile_id      = dto.profile_id.value;
    out.operation_state  = static_cast<int32_t>(dto.operation_state);
    out.timestamp       = dto.timestamp.value;
    out.total_size      = dto.total_size;
    out.current_size    = dto.current_size;

    out.name = str;
    memcpy(str, dto.name.c_str(), dto.name.size() + 1);
    str += dto.name.size() + 1;

    out.path = str;
    memcpy(str, dto.path.c_str(), dto.path.size() + 1);
    str += dto.path.size() + 1;

    out.origin_uri = str;
    memcpy(str, dto.origin_uri.c_str(), dto.origin_uri.size() + 1);
    str += dto.origin_uri.size() + 1;
}

static void notifyDownloadDto(DownloadChangedCallback cb, const orion::DownloadDto& dto)
{
    if (!cb) return;
    size_t blob = sizeof(DownloadEntryNative)
        + dto.name.size() + 1
        + dto.path.size() + 1
        + dto.origin_uri.size() + 1;
    auto buf = std::make_unique<char[]>(blob);
    auto* entry = reinterpret_cast<DownloadEntryNative*>(buf.get());
    char* str = buf.get() + sizeof(DownloadEntryNative);
    fillDownloadNative(*entry, dto, str);
    cb(entry);
}

// ── Bridge functions called by engine download callbacks ──────────────────

static std::string getRawSetting(int64_t profileId, const char* key, const char* realm,
    const char* keyName, const char* type, const char* defaultValue)
{
    if (!g_core_api || profileId == 0) return defaultValue;
    orion::GetSettingQuery query{};
    query.profile_id = orion::ProfileId(profileId);
    query.key = key;
    query.realm = realm;
    query.key_name = keyName;
    query.type = type;
    query.default_value = defaultValue;
    std::string value;
    bool done = false;
    g_core_api->get_setting_raw(query,
        [&](orion::GetSettingReply<std::string> reply) {
            value = (reply.err || reply.value.empty()) ? defaultValue : reply.value;
            done = true;
        });
    while (!done) orion::tls_run_loop()->pump();
    return value;
}

bool bridgeGetDownloadFolder(int64_t profileId, std::wstring& folderPath)
{
    // FileDownloadLocation: downloads=1, ask_for_each_download=2, custom_path=3
    std::string raw = getRawSetting(profileId,
        "settings::general::file_download_location", "settings::general",
        "file_download_location", "integer", "2");
    int locationValue = std::atoi(raw.c_str());

    switch (locationValue) {
    case 1: // downloads (system folder)
        return getKnownFolderPath(FOLDERID_Downloads, folderPath);
    case 3: { // custom_path
        std::string customPath = getRawSetting(profileId,
            "settings::general::file_download_custom_path", "settings::general",
            "file_download_custom_path", "text", "");
        if (!customPath.empty()) {
            folderPath = widen(customPath);
            return true;
        }
        return getKnownFolderPath(FOLDERID_Downloads, folderPath);
    }
    default: // ask_for_each_download (2) or not_set (0) — use Downloads folder
        return getKnownFolderPath(FOLDERID_Downloads, folderPath);
    }
}

bool bridgeShouldAskDownloadLocation(int64_t profileId)
{
    std::string raw = getRawSetting(profileId,
        "settings::general::file_download_location", "settings::general",
        "file_download_location", "integer", "2");
    return std::atoi(raw.c_str()) == 2;
}

void bridgeUpdateDownloadPath(int64_t downloadId, const std::string& name,
    const std::string& path)
{
    if (!g_core_api || downloadId == 0) return;
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(downloadId);
    q.update_name = true;
    q.name = name;
    q.update_path = true;
    q.path = path;
    g_core_api->update_downloads({q}, [](orion::Error) {});
}

int64_t bridgeCreateDownload(int64_t profileId, const std::string& name,
    const std::string& path, const std::string& originUri, int64_t totalSize)
{
    if (!g_core_api || profileId == 0) return 0;
    orion::CreateDownloadQuery q;
    q.profile_id = orion::ProfileId(profileId);
    q.name = name;
    q.path = path;
    q.origin_uri = originUri;
    q.total_size = totalSize;
    q.operation_state = orion::DownloadOperationState::downloading;
    q.timestamp = orion::UnixTimeUtc64(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());

    int64_t result_id = 0;
    bool done = false;
    g_core_api->create_download(q, [&](orion::CreateDownloadReply reply) {
        if (!reply.err) result_id = reply.id.value;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result_id;
}

void bridgeUpdateDownloadProgress(int64_t downloadId, int64_t currentSize,
    int64_t totalSize)
{
    if (!g_core_api || downloadId == 0) return;
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(downloadId);
    q.update_current_size = true;
    q.current_size = currentSize;
    if (totalSize > 0) {
        q.update_total_size = true;
        q.total_size = totalSize;
    }
    g_core_api->update_downloads({q}, [](orion::Error) {});
}

void bridgeFinishDownload(int64_t downloadId, int64_t finalSize)
{
    if (!g_core_api || downloadId == 0) return;
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(downloadId);
    q.update_operation_state = true;
    q.operation_state = orion::DownloadOperationState::finished;
    q.update_current_size = true;
    q.current_size = finalSize;
    q.update_total_size = true;
    q.total_size = finalSize;
    g_core_api->update_downloads({q}, [](orion::Error) {});
}

void bridgeFailDownload(int64_t downloadId)
{
    if (!g_core_api || downloadId == 0) return;
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(downloadId);
    q.update_operation_state = true;
    q.operation_state = orion::DownloadOperationState::failed;
    g_core_api->update_downloads({q}, [](orion::Error) {});
}

static std::unordered_map<int64_t, std::function<void()>> g_cancel_handlers;

void bridgeRegisterCancelHandler(int64_t downloadId, std::function<void()> handler)
{
    if (downloadId > 0)
        g_cancel_handlers[downloadId] = std::move(handler);
}

bool bridgeUnregisterCancelHandler(int64_t downloadId)
{
    return g_cancel_handlers.erase(downloadId) > 0;
}

bool bridgeCancelDownload(int64_t downloadId)
{
    auto it = g_cancel_handlers.find(downloadId);
    if (it == g_cancel_handlers.end())
        return false;
    auto handler = std::move(it->second);
    g_cancel_handlers.erase(it);
    handler();
    return true;
}

extern "C" __declspec(dllexport) void CancelDownload(int64_t downloadId)
{
    bridgeCancelDownload(downloadId);

    if (!g_core_api || downloadId == 0) return;
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(downloadId);
    q.update_operation_state = true;
    q.operation_state = orion::DownloadOperationState::cancelled;
    g_core_api->update_downloads({q}, [](orion::Error) {});
}

static std::unordered_map<std::string, PendingRestart> g_pending_restarts;

bool bridgeHasPendingRestart(const std::string& url)
{
    return g_pending_restarts.count(url) > 0;
}

PendingRestart bridgeConsumePendingRestart(const std::string& url)
{
    auto it = g_pending_restarts.find(url);
    if (it == g_pending_restarts.end())
        return {};
    auto result = std::move(it->second);
    g_pending_restarts.erase(it);
    return result;
}

extern "C" __declspec(dllexport) void RestartDownload(int64_t downloadId)
{
    if (!g_core_api || downloadId == 0) return;
    BrowserEngine* engine = orionLoadedEngine();
    if (!engine)
        abort();

    std::vector<orion::DownloadDto> downloads;
    bool done = false;
    g_core_api->get_downloads(
        { orion::DownloadId(downloadId) },
        [&](orion::GetDownloadsReply reply) {
            if (!reply.err) downloads = std::move(reply.downloads);
            done = true;
        });
    while (!done) orion::tls_run_loop()->pump();

    if (downloads.empty() || downloads[0].origin_uri.empty()) return;
    auto& dto = downloads[0];

    // Reset the DB entry to downloading state
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(downloadId);
    q.update_operation_state = true;
    q.operation_state = orion::DownloadOperationState::downloading;
    q.update_current_size = true;
    q.current_size = 0;
    g_core_api->update_downloads({q}, [](orion::Error) {});

    // Register pending restart so the engine download flow reuses this entry
    g_pending_restarts[dto.origin_uri] = { downloadId, dto.path };

    engine->startDownload(dto.origin_uri.c_str());
}

extern "C" __declspec(dllexport) void SetDownloadCallbacks(
    DownloadChangedCallback createdCb,
    DownloadChangedCallback updatedCb,
    DownloadDeletedCallback deletedCb)
{
    g_download_created_cb = createdCb;
    g_download_updated_cb = updatedCb;
    g_download_deleted_cb = deletedCb;

    if (g_core_api) {
        if (g_downloads_observer != orion::ObserverId{})
            g_core_api->downloads_change().remove_observer(g_downloads_observer);

        g_downloads_observer = g_core_api->downloads_change().add_observer(
            [](orion::DownloadsChange change) {
                for (auto& dto : change.created)
                    notifyDownloadDto(g_download_created_cb, dto);
                for (auto& dto : change.updated)
                    notifyDownloadDto(g_download_updated_cb, dto);
                for (auto& id : change.deleted) {
                    if (g_download_deleted_cb)
                        g_download_deleted_cb(id.value);
                }
            });
    }
}

// -----------------------------------------------------------------------
// Notifications (notification_bridge.h, Orion/Services/NotificationService.cs)
// -----------------------------------------------------------------------

// C# installs its desktop-notification presenter. C# must keep both
// delegates rooted for the process lifetime.
extern "C" __declspec(dllexport) void SetNotificationCallbacks(
    ShowNotificationCallback showCb, ClearNotificationCallback clearCb)
{
    orion::HostNotificationProvider::set_callbacks(showCb, clearCb);
}

// C# reports user/platform interaction with a shown notification. UI thread
// only: the core runs the extension listeners synchronously from here.
// `type` / `source` are orion::NotificationEventType / orion::NotificationSource.
extern "C" __declspec(dllexport) void NotifyNotificationEvent(
    int32_t type, int32_t source, const char* source_id, const char* notification_id,
    int32_t button_index, int32_t by_user)
{
    if (!g_core_api || !source_id || !notification_id)
        return;
    orion::NotificationEvent event;
    event.type = static_cast<orion::NotificationEventType>(type);
    event.source = static_cast<orion::NotificationSource>(source);
    event.source_id = source_id;
    event.notification_id = notification_id;
    event.button_index = button_index;
    event.by_user = by_user != 0;
    g_core_api->notification_event().notify(event);
}

extern "C" __declspec(dllexport) DownloadEntryNative* GetDownloads(int64_t profile_id, int* outCount)
{
    *outCount = 0;
    if (!g_core_api || profile_id == 0) return nullptr;

    std::vector<orion::DownloadId> ids;
    bool ids_done = false;
    orion::GetDownloadIdsQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    g_core_api->get_download_ids(q, [&](orion::GetDownloadIdsReply reply) {
        if (!reply.err) ids = std::move(reply.ids);
        ids_done = true;
    });
    while (!ids_done) orion::tls_run_loop()->pump();
    if (ids.empty()) return nullptr;

    std::vector<orion::DownloadDto> downloads;
    bool done = false;
    g_core_api->get_downloads(ids, [&](orion::GetDownloadsReply reply) {
        if (!reply.err) downloads = std::move(reply.downloads);
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();

    int count = static_cast<int>(downloads.size());
    if (count == 0) return nullptr;

    size_t blob_size = count * sizeof(DownloadEntryNative);
    for (auto& d : downloads)
        blob_size += d.name.size() + 1 + d.path.size() + 1 + d.origin_uri.size() + 1;

    auto* base = static_cast<char*>(malloc(blob_size));
    if (!base) return nullptr;

    auto* arr = reinterpret_cast<DownloadEntryNative*>(base);
    char* str = base + count * sizeof(DownloadEntryNative);
    for (int i = 0; i < count; i++)
        fillDownloadNative(arr[i], downloads[i], str);

    *outCount = count;
    return arr;
}

extern "C" __declspec(dllexport) void FreeDownloads(DownloadEntryNative* entries)
{
    free(entries);
}

extern "C" __declspec(dllexport) void DeleteDownloads(const int64_t* ids, int count)
{
    if (!g_core_api || !ids || count <= 0) return;
    orion::DeleteDownloadsQuery query;
    for (int i = 0; i < count; i++)
        query.ids.push_back(orion::DownloadId(ids[i]));
    g_core_api->delete_downloads(query, [](orion::DeleteDownloadsReply) {});
}

extern "C" __declspec(dllexport) int64_t CreateDownload(
    int64_t profile_id, const char* name, const char* path,
    int32_t operation_state, int64_t total_size, int64_t current_size,
    const char* origin_uri)
{
    if (!g_core_api || profile_id == 0) return 0;
    orion::CreateDownloadQuery q;
    q.profile_id = orion::ProfileId(profile_id);
    q.name = name ? name : "";
    q.path = path ? path : "";
    q.operation_state = static_cast<orion::DownloadOperationState>(operation_state);
    q.total_size = total_size;
    q.current_size = current_size;
    q.origin_uri = origin_uri ? origin_uri : "";
    q.timestamp = orion::UnixTimeUtc64(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());

    int64_t result_id = 0;
    bool done = false;
    g_core_api->create_download(q, [&](orion::CreateDownloadReply reply) {
        if (!reply.err) result_id = reply.id.value;
        done = true;
    });
    while (!done) orion::tls_run_loop()->pump();
    return result_id;
}

extern "C" __declspec(dllexport) void UpdateDownload(
    int64_t download_id, int32_t operation_state,
    int64_t total_size, int64_t current_size,
    const char* name, const char* path)
{
    if (!g_core_api || download_id == 0) return;
    orion::UpdateDownloadQuery q;
    q.id = orion::DownloadId(download_id);
    q.update_operation_state = true;
    q.operation_state = static_cast<orion::DownloadOperationState>(operation_state);
    q.update_total_size = true;
    q.total_size = total_size;
    q.update_current_size = true;
    q.current_size = current_size;
    if (name) {
        q.update_name = true;
        q.name = name;
    }
    if (path) {
        q.update_path = true;
        q.path = path;
    }
    g_core_api->update_downloads({q}, [](orion::Error) {});
}

