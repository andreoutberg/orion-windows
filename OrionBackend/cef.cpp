#include "pch.h"
#include "engine_impl.h"
#include "host_api.h"
#include "utils.h"
#include "common/src/log.h"
#include <src/file_system.h>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_command_line.h"
#include "include/cef_task.h"
#include "include/cef_parser.h"
#include "include/cef_values.h"
#include "include/cef_request_context.h"
#include "include/cef_sandbox_win.h"
#include "include/cef_version.h"
#include "include/cef_devtools_message_observer.h"
#include "include/base/cef_logging.h"
#include "include/base/cef_callback.h"
#include "include/wrapper/cef_closure_task.h"
#include "include/cef_download_handler.h"
#include "include/cef_context_menu_handler.h"
#include "include/cef_find_handler.h"
#include "include/cef_permission_handler.h"
#include "include/cef_jsdialog_handler.h"

#include "download_bridge.h"
#include "window/orion_webview_tabview_widgets/scripts/autofill_scripts.h"

#include <memory>
#include <shlobj.h>

// ---------------------------------------------------------------------------
// CEF browser engine — windowed implementation with external message pump.
//
// CEF's browser UI thread is the WinUI thread. We drive CEF's message loop
// via CefDoMessageLoopWork() called from C# whenever a Win32 auto-reset
// event is signalled by OnScheduleMessagePumpWork(). This mirrors the
// proven pattern used for TickOrionCore() and avoids relying on WinUI's
// ASTA message loop to dispatch messages to a custom window.
// ---------------------------------------------------------------------------

// Auto-reset event signalled by OnScheduleMessagePumpWork().
// C# waits on this and calls PumpCefMessageLoop() on the UI thread.
static HANDLE g_cef_wakeup_event = nullptr;

// ---------------------------------------------------------------------------
// CefApp — external message pump integration.
// ---------------------------------------------------------------------------

class OrionCefApp : public CefApp,
                    public CefBrowserProcessHandler
{
public:
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override
    {
        return this;
    }

    void OnBeforeCommandLineProcessing(
        const CefString& /*process_type*/,
        CefRefPtr<CefCommandLine> command_line) override
    {
        // Blink features whose flow needs browser UI or backends this
        // integration doesn't provide. Sites feature-detect them and fall
        // back to paths Orion serves; exposed-but-unservable means dead-end
        // buttons and prompts that alloy style silently ignores.
        //  - UserMediaElement(+Legacy): PEPC <usermedia> element — consent
        //    must flow through getUserMedia and the app's consent seam, not
        //    Chrome's prompt path (OnShowPermissionPrompt).
        //  - FedCm: account-chooser UI; absent, sign-in libraries fall back
        //    to their popup flow.
        //  - WebUSB/Serial/WebHID/WebBluetooth: need device-chooser UI (CEF
        //    has only file choosers). Absent under the WebKit engine too.
        //  - Notifications/PushMessaging: no display path or push backend;
        //    the permission can never be granted, so sites nag for nothing.
        // WebAuthn and WebShare stay enabled: Windows serves both through OS
        // surfaces (Windows Security dialog / share sheet), no Chrome UI.
        //
        // Also disabled, on principle rather than for UI reasons: the Privacy
        // Sandbox advertising stack (Topics, Attribution Reporting, Protected
        // Audience/FLEDGE, Shared Storage, Private State Tokens, Fenced
        // Frames) — vendor-unilateral tracking/advertising APIs with formal
        // negative positions from Mozilla and Apple; Orion refuses them
        // regardless of engine.
        command_line->AppendSwitchWithValue(
            "disable-blink-features",
            "UserMediaElement,UserMediaElementLegacy,FedCm,WebUSB,Serial,"
            "WebHID,WebBluetooth,Notifications,PushMessaging,"
            "TopicsAPI,TopicsDocumentAPI,TopicsImgAPI,AttributionReporting,"
            "Fledge,AdInterestGroupAPI,SharedStorageAPI,"
            "InterestGroupsInSharedStorageWorklet,PrivateStateTokens,"
            "FencedFrames");

        // PaymentRequest (needs the payment-sheet UI) ignores blink feature
        // flags: ApplyWebPreferences re-drives it per WebView from the
        // kWebPayments base feature, so disable that instead.
        std::string disabledFeatures =
            command_line->GetSwitchValue("disable-features").ToString();
        command_line->AppendSwitchWithValue("disable-features",
            disabledFeatures.empty() ? "WebPayments"
                                     : disabledFeatures + ",WebPayments");

        // Origin trials let token-holding sites enable experimental Blink
        // features per page, validated against a Google key baked into
        // libcef — features that can assume Chrome UI Orion doesn't have
        // (PEPC above reached us this way). Replacing the key with one that
        // signed nothing disables all trials, deprecation trials included,
        // like Android WebView's experimental-trial ban. The parser only
        // accepts well-formed 32-byte base64; on parse failure Google's
        // default key stays active.
        command_line->AppendSwitchWithValue("origin-trial-public-key",
                                            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        // Route HTTP auth challenges (401/407) to GetAuthCredentials instead
        // of Chrome's LoginHandler: CreateLoginDelegate prefers Chrome's
        // tab-modal login UI whenever the request has a WebContents, and that
        // UI cannot render in our embedding — challenges died with
        // ERR_INVALID_AUTH_CREDENTIALS before ever reaching the client API.
        command_line->AppendSwitch("disable-chrome-login-prompt");

        // Disable CEF's own DirectComposition — it conflicts with WinUI's
        // DirectComposition layer when the browser HWND has WS_EX_LAYERED.
        //command_line->AppendSwitch("disable-direct-composition");

        // Run the GPU service in the browser process to reduce IPC overhead.
        //command_line->AppendSwitch("in-process-gpu");

        // Enable Chromium logging to stderr (shows in VS Output/debugger).
        //command_line->AppendSwitchWithValue("enable-logging", "stderr");
        //command_line->AppendSwitchWithValue("v", "1");

    }

    // Called by CEF (from any thread) when work is available.
    // We signal the wakeup event so the C# watcher thread enqueues a pump
    // on the UI thread via DispatcherQueue.
    void OnScheduleMessagePumpWork(int64_t /*delay_ms*/) override
    {
        if (g_cef_wakeup_event)
            SetEvent(g_cef_wakeup_event);
    }

    IMPLEMENT_REFCOUNTING(OrionCefApp);
};

static CefRefPtr<OrionCefApp> g_cef_app;

// ---------------------------------------------------------------------------
// CEF logging — route CEF wrapper log messages through orion::log_*.
// ScopedEarlySupport handles pre-CefInitialize logging from libcef_dll_wrapper.
// Post-init logging is controlled by CefSettings.log_severity/log_file.
// ---------------------------------------------------------------------------

static bool cefLogHandler(const char* log_line)
{
    // Route to orion::log_info — the log_line already includes severity prefix.
    orion::log_info("CEF: %s", log_line);
    return true; // suppress default stderr/OutputDebugString
}

static cef::logging::ScopedEarlySupport::Config g_cef_log_config = {
    .min_log_level = cef::logging::LOG_INFO,
    .vlog_level = 0,
    .log_prefix = "CEF",
    .log_process_id = false,
    .log_thread_id = true,
    .log_timestamp = false,
    .log_tickcount = false,
    .formatted_log_handler = cefLogHandler,
};
static cef::logging::ScopedEarlySupport g_cef_early_logging(g_cef_log_config);

// Owned by the CEF bootstrap for the lifetime of RunWinMain. Both the broker
// and its children must enter CEF with their own process's sandbox services.
static void* g_sandbox_info = nullptr;

int checkBrowserSubprocess()
{
    if (!g_sandbox_info) return ERROR_INVALID_STATE;
    CefMainArgs main_args(GetModuleHandle(nullptr));
    if (!g_cef_app) g_cef_app = new OrionCefApp();
    return CefExecuteProcess(main_args, g_cef_app, g_sandbox_info);
}

int runManagedBrowser();

CEF_BOOTSTRAP_EXPORT int RunWinMain(HINSTANCE, LPWSTR, int, void* sandbox_info,
                                    cef_version_info_t* version)
{
    if (!sandbox_info || !version || version->cef_commit_number != CEF_COMMIT_NUMBER)
        return ERROR_REVISION_MISMATCH;
    g_sandbox_info = sandbox_info;
    const int code = checkBrowserSubprocess();
    if (code >= 0) return code;
    auto args = CefCommandLine::CreateCommandLine();
    args->InitFromString(GetCommandLineW());
    if (args->HasSwitch("no-sandbox") || args->HasSwitch("single-process")
        || args->HasSwitch("disable-gpu-sandbox") || args->HasSwitch("in-process-gpu")) {
        OutputDebugStringW(L"Orion requires Chromium's multiprocess sandbox.");
        return ERROR_INVALID_PARAMETER;
    }
    return runManagedBrowser();
}

static bool g_cef_initialized = false;
static CefRefPtr<CefRequestContext> g_request_context;
static bool g_shutting_down = false;
// Browsers created (OnAfterCreated) and not yet destroyed (OnBeforeClose).
// cleanBrowserEngine() pumps until this drains to zero before CefShutdown().
static int  g_live_browser_count = 0;
// Profile this process's CEF instance is bound to (set at CefInitialize;
// one profile per process).
static int64_t g_cef_profile_id = 0;

// ---------------------------------------------------------------------------
// Autofill script injection.
// ---------------------------------------------------------------------------

// Polyfill so the GTK autofill scripts (which call
// window.webkit.messageHandlers.X.postMessage()) work in CEF.
// Routes messages through console.log with a parseable prefix.
static const char* s_messageHandlersShim = R"js(
if (!window.webkit) {
    window.webkit = { messageHandlers: {} };
    window.webkit.messageHandlers['inputFocus'] = {
        postMessage: function(data) {
            console.log('__ORION_MSG__:inputFocus:' + JSON.stringify(data));
        }
    };
    window.webkit.messageHandlers['formSubmit'] = {
        postMessage: function() {
            console.log('__ORION_MSG__:formSubmit');
        }
    };
}
)js";

static void injectAutofillScripts(CefRefPtr<CefFrame> frame)
{
    std::string url = frame->GetURL();
    frame->ExecuteJavaScript(s_messageHandlersShim, url, 0);
    frame->ExecuteJavaScript(orion::gtk::get_autofill_script(), url, 0);
    frame->ExecuteJavaScript(orion::gtk::get_input_focus_script(), url, 0);
    frame->ExecuteJavaScript(orion::gtk::get_form_submit_script(), url, 0);
}

// ---------------------------------------------------------------------------
// Per-browser state.
// ---------------------------------------------------------------------------

struct BrowserEntry
{
    CefRefPtr<CefBrowser> browser;
    CefRefPtr<CefBrowser> devToolsBrowser;  // the frontend inspecting `browser`, while open
    BrowserCallbacks callbacks;
    HWND mainWindowHwnd = nullptr;
    HWND popupParentHwnd = nullptr;
    bool errorPagePending = false;
    std::string customUserAgent;
    CefRefPtr<CefRegistration> devToolsRegistration;
    int nextDevToolsMsgId = 1;
    int pendingSnapshotMsgId = 0;  // 0 = no snapshot in flight
    int64_t profileId = 0;
    bool isElementFullscreen = false;
    bool closeInitiated = false;   // close came from our code, not the page
};

// ---------------------------------------------------------------------------
// Tab snapshot capture — requests a screenshot of the current viewport via
// the DevTools protocol (Page.captureScreenshot); the JPEG bytes are
// delivered through the view's snapshotTaken callback.
// ---------------------------------------------------------------------------

static void requestSnapshot(BrowserEntry& entry, int32_t /*maxPixelWidth*/)
{
    // maxPixelWidth is currently ignored: downscaling via the CDP clip.scale
    // parameter correlated with renderer crashes (blink DCHECK), so CEF
    // captures at native viewport size for now and the consumer scales.
    if (!entry.browser) return;
    auto params = CefDictionaryValue::Create();
    // Thumbnail-quality JPEG: results of any size arrive intact (verified
    // to 6.8MB), but base64 JSON round-trips make large captures needlessly
    // expensive to transport and parse.
    params->SetString("format", "jpeg");
    params->SetInt("quality", 60);
    // Pass 0 and use the returned id: explicit ids that collide with one
    // already used (e.g. by applyColorSchemeOverride's auto-assigned call)
    // are silently replaced by CEF, so the passed-in value can't be trusted.
    int msgId = entry.browser->GetHost()->ExecuteDevToolsMethod(
        0, "Page.captureScreenshot", params);
    if (msgId != 0)
        entry.pendingSnapshotMsgId = msgId;
    else if (entry.callbacks.snapshotTaken)
        entry.callbacks.snapshotTaken(nullptr, 0);
}

// DevTools observer that receives method results for this browser: snapshot
// results are routed by message id; everything else is treated as a
// Runtime.evaluate result for credential scraping (passwords flow through
// CEF's internal DevTools pipe instead of console.log).
class OrionDevToolsObserver : public CefDevToolsMessageObserver
{
public:
    explicit OrionDevToolsObserver(std::shared_ptr<BrowserEntry> entry)
        : m_entry(std::move(entry))
    {}

    void OnDevToolsMethodResult(CefRefPtr<CefBrowser> /*browser*/,
                                int message_id,
                                bool success,
                                const void* result,
                                size_t result_size) override
    {
        if (m_entry->pendingSnapshotMsgId != 0 &&
            message_id == m_entry->pendingSnapshotMsgId) {
            m_entry->pendingSnapshotMsgId = 0;
            handleSnapshotResult(success, result, result_size);
            return;
        }

        if (!success || !result || result_size == 0) return;
        if (!m_entry->callbacks.formSubmit) return;

        auto parsed = CefParseJSON(result, result_size, JSON_PARSER_RFC);
        if (!parsed) return;

        auto dict = parsed->GetDictionary();
        if (!dict || !dict->HasKey("result")) return;

        auto resultDict = dict->GetDictionary("result");
        if (!resultDict || resultDict->GetString("type") != "object") return;

        // Runtime.evaluate wraps the object in a "result" with preview/properties.
        // For simple objects, the value is serialized via "value" when
        // returnByValue is true.
        if (!resultDict->HasKey("value")) return;
        auto valueDict = resultDict->GetDictionary("value");
        if (!valueDict) return;

        std::string username, password;
        if (valueDict->HasKey("username") &&
            valueDict->GetType("username") == VTYPE_STRING)
            username = valueDict->GetString("username");
        if (valueDict->HasKey("password") &&
            valueDict->GetType("password") == VTYPE_STRING)
            password = valueDict->GetString("password");

        if (username.empty() || password.empty()) return;

        auto outDict = CefDictionaryValue::Create();
        outDict->SetString("username", username);
        outDict->SetString("password", password);
        auto outVal = CefValue::Create();
        outVal->SetDictionary(outDict);
        std::string json = CefWriteJSON(outVal, JSON_WRITER_DEFAULT);
        m_entry->callbacks.formSubmit(json.c_str());
    }

private:
    void handleSnapshotResult(bool success, const void* result, size_t result_size)
    {
        if (!m_entry->callbacks.snapshotTaken)
            return;

        if (!success || !result || result_size == 0) {
            m_entry->callbacks.snapshotTaken(nullptr, 0);
            return;
        }

        auto parsed = CefParseJSON(result, result_size, JSON_PARSER_RFC);
        auto dict = parsed ? parsed->GetDictionary() : nullptr;
        if (!dict || !dict->HasKey("data")) {
            m_entry->callbacks.snapshotTaken(nullptr, 0);
            return;
        }

        auto decoded = CefBase64Decode(dict->GetString("data"));
        size_t decodedSize = decoded ? decoded->GetSize() : 0;
        if (!decodedSize) {
            m_entry->callbacks.snapshotTaken(nullptr, 0);
            return;
        }

        std::vector<unsigned char> img(decodedSize);
        decoded->GetData(img.data(), decodedSize, 0);
        m_entry->callbacks.snapshotTaken(img.data(), static_cast<int32_t>(decodedSize));
    }

    std::shared_ptr<BrowserEntry> m_entry;
    IMPLEMENT_REFCOUNTING(OrionDevToolsObserver);
};

static std::unordered_map<HWND, std::shared_ptr<BrowserEntry>> g_browsers;

// Engine preferences, stored here because CEF may not be initialized yet
// when a setting arrives; applied as profile preferences once it is
// (see applyEnginePreferences).
static int g_minimum_font_size = 0;  // px; 0 = no minimum
static bool g_javascript_enabled = true;
// Chromium's built-in popup blocker: -1 = untouched (Chromium default,
// blocks gesture-less popups before OnBeforePopup fires), 0 = disabled
// (the app's popup setting owns the policy), 1 = enabled.
static int g_native_popup_blocking = -1;
static void applyEnginePreferences();

// Process-default user agent (CefSettings.user_agent: User-Agent header and
// navigator.userAgent for every view). Resolved from the profile's setting
// by C# after LoadBrowserEngine; latched here because CEF initializes lazily
// at the first createBrowserView.
static std::wstring g_default_user_agent;

// ---------------------------------------------------------------------------
// Media-access permission requests (getUserMedia / getDisplayMedia).
// Each request is parked here until C# resolves it via
// RespondToMediaAccessPermissionRequest; dropping an entry's callback ref
// makes CEF cancel (deny) the request, which is how view close and engine
// shutdown clean up pending requests.
// ---------------------------------------------------------------------------

struct PendingMediaAccessRequest
{
    CefRefPtr<CefMediaAccessCallback> callback;
    uint32_t requestedPermissions = 0;
    int browserId = 0;
};

static std::unordered_map<int64_t, PendingMediaAccessRequest> g_pendingMediaRequests;
static int64_t g_nextMediaRequestId = 1;

// ---------------------------------------------------------------------------
// HTTP-auth challenges (401 from a server / 407 from a proxy), parked the
// same way as media requests until C# resolves them via
// RespondToAuthCredentials. Entries are cancelled explicitly when their view
// closes or the engine shuts down.
// ---------------------------------------------------------------------------

struct PendingAuthRequest
{
    CefRefPtr<CefAuthCallback> callback;
    int browserId = 0;
};

static std::unordered_map<int64_t, PendingAuthRequest> g_pendingAuthRequests;
static int64_t g_nextAuthRequestId = 1;

static int32_t toWebMediaPermissions(uint32_t cefPermissions)
{
    int32_t result = WebMediaPermissionNone;
    if (cefPermissions & CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE)
        result |= WebMediaPermissionCamera;
    if (cefPermissions & CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE)
        result |= WebMediaPermissionMicrophone;
    if (cefPermissions & (CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE |
                          CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE))
        result |= WebMediaPermissionScreenShare;
    return result;
}

// Effective UI appearance for prefers-color-scheme. Forced per browser via
// DevTools emulation: alloy-style browsers' web prefs otherwise follow the OS
// NativeTheme, which official CEF builds cannot re-drive at runtime. Applied
// in OnAfterCreated and swept over live browsers on change.
static bool g_useDarkAppearance = false;

static void applyColorSchemeOverride(CefRefPtr<CefBrowser> browser)
{
    auto feature = CefDictionaryValue::Create();
    feature->SetString("name", "prefers-color-scheme");
    feature->SetString("value", g_useDarkAppearance ? "dark" : "light");
    auto features = CefListValue::Create();
    features->SetSize(1);
    features->SetDictionary(0, feature);
    auto params = CefDictionaryValue::Create();
    params->SetList("features", features);
    browser->GetHost()->ExecuteDevToolsMethod(0, "Emulation.setEmulatedMedia", params);
}

// Per-view UA override (DevTools emulation): drives navigator.userAgent and,
// because no userAgentMetadata is provided, disables Sec-CH-UA client hints
// for the view's navigations. An empty string clears back to the process
// default. Takes effect from the next navigation.
static void applyUserAgentOverride(BrowserEntry& entry)
{
    if (!entry.browser) return;
    auto params = CefDictionaryValue::Create();
    params->SetString("userAgent", entry.customUserAgent);
    entry.browser->GetHost()->ExecuteDevToolsMethod(0, "Emulation.setUserAgentOverride", params);
}



// ---------------------------------------------------------------------------
// Per-request header pass: overrides the User-Agent header when a custom UA
// is set, and always strips Sec-CH-UA* client-hint headers — their brand
// metadata comes from Chromium's version, not the configured UA, so they
// would contradict it.
// ---------------------------------------------------------------------------

class UserAgentRequestHandler : public CefResourceRequestHandler
{
public:
    explicit UserAgentRequestHandler(const std::string& ua)
        : m_ua(ua) {}

    cef_return_value_t OnBeforeResourceLoad(
        CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
        CefRefPtr<CefRequest> request, CefRefPtr<CefCallback>) override
    {
        CefRequest::HeaderMap headers;
        request->GetHeaderMap(headers);
        for (auto it = headers.begin(); it != headers.end();) {
            if (_wcsnicmp(it->first.ToWString().c_str(), L"sec-ch-ua", 9) == 0)
                it = headers.erase(it);
            else
                ++it;
        }
        if (!m_ua.empty()) {
            headers.erase("User-Agent");
            headers.insert(std::make_pair("User-Agent", m_ua));
        }
        request->SetHeaderMap(headers);
        return RV_CONTINUE;
    }

private:
    std::string m_ua;
    IMPLEMENT_REFCOUNTING(UserAgentRequestHandler);
};

// ---------------------------------------------------------------------------
// CefClient — routes CEF events to BrowserCallbacks.
// With external_message_pump, these fire on the WinUI thread directly.
// ---------------------------------------------------------------------------

// Maps CEF's window-open disposition (computed by Blink from the click's
// modifiers/button) to an OpenDisposition. Returns false for dispositions
// that should navigate the current view normally.
static bool toOpenDisposition(cef_window_open_disposition_t cefDisposition, int32_t& disposition)
{
    switch (cefDisposition) {
    case CEF_WOD_NEW_FOREGROUND_TAB: disposition = OpenDisposition::NewForegroundTab; return true;
    case CEF_WOD_NEW_BACKGROUND_TAB: disposition = OpenDisposition::NewBackgroundTab; return true;
    case CEF_WOD_NEW_WINDOW:         disposition = OpenDisposition::NewWindow;        return true;
    default: return false;
    }
}

// Keyboard routing shared by the page client and the DevTools client: app
// shortcuts work the same with focus inside DevTools as on the page.
// Reserved shortcuts (Ctrl+T, Ctrl+W, ...) fire before the engine sees the
// key so web content cannot override them; the rest reach C# only when the
// engine left the key unhandled.
static bool forwardReservedKey(const BrowserEntry& entry, const CefKeyEvent& event)
{
    if (g_shutting_down) return false;
    if (event.type != KEYEVENT_RAWKEYDOWN) return false;
    if (!entry.callbacks.reservedKeyEvent) return false;

    uint32_t vkCode = static_cast<uint32_t>(event.windows_key_code);
    uint32_t modifiers = toVirtualKeyModifiers(
        event.modifiers & EVENTFLAG_CONTROL_DOWN,
        event.modifiers & EVENTFLAG_ALT_DOWN,
        event.modifiers & EVENTFLAG_SHIFT_DOWN);
    return entry.callbacks.reservedKeyEvent(vkCode, modifiers) != 0;
}

static bool forwardUnhandledKey(const BrowserEntry& entry, const CefKeyEvent& event)
{
    if (g_shutting_down) return false;
    if (event.type != KEYEVENT_RAWKEYDOWN) return false;
    if (!entry.callbacks.unhandledKeyEvent) return false;

    uint32_t vkCode = static_cast<uint32_t>(event.windows_key_code);
    uint32_t modifiers = toVirtualKeyModifiers(
        event.modifiers & EVENTFLAG_CONTROL_DOWN,
        event.modifiers & EVENTFLAG_ALT_DOWN,
        event.modifiers & EVENTFLAG_SHIFT_DOWN);
    return entry.callbacks.unhandledKeyEvent(vkCode, modifiers) != 0;
}

class OrionCefClient : public CefClient,
                       public CefDisplayHandler,
                       public CefLoadHandler,
                       public CefLifeSpanHandler,
                       public CefKeyboardHandler,
                       public CefRequestHandler,
                       public CefDownloadHandler,
                       public CefContextMenuHandler,
                       public CefFindHandler,
                       public CefPermissionHandler,
                       public CefJSDialogHandler
{
public:
    explicit OrionCefClient(std::shared_ptr<BrowserEntry> entry)
        : m_entry(std::move(entry))
    {}

    CefRefPtr<CefDisplayHandler>     GetDisplayHandler()     override { return this; }
    CefRefPtr<CefLoadHandler>        GetLoadHandler()        override { return this; }
    CefRefPtr<CefLifeSpanHandler>    GetLifeSpanHandler()    override { return this; }
    CefRefPtr<CefKeyboardHandler>    GetKeyboardHandler()    override { return this; }
    CefRefPtr<CefRequestHandler>     GetRequestHandler()     override { return this; }
    CefRefPtr<CefDownloadHandler>    GetDownloadHandler()    override { return this; }
    CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }
    CefRefPtr<CefFindHandler>        GetFindHandler()        override { return this; }
    CefRefPtr<CefPermissionHandler>  GetPermissionHandler()  override { return this; }
    CefRefPtr<CefJSDialogHandler>    GetJSDialogHandler()    override { return this; }

    // CefJSDialogHandler — only beforeunload for now. CEF's default
    // beforeunload dialog cannot receive input in our foreign-HWND embedding,
    // and an unanswered dialog deadlocks the close path (destroyBrowserView
    // pumps until OnBeforeClose, which never comes). Auto-continue: the page
    // closes without a leave-site prompt. alert/confirm/prompt keep the CEF
    // default until a proper app-drawn dialog exists.
    // TODO: route JS dialogs (this one included) to a C# PopupDialog like
    // media permissions, so sites can warn about unsaved changes.
    bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser> /*browser*/,
                              const CefString& /*message_text*/,
                              bool /*is_reload*/,
                              CefRefPtr<CefJSDialogCallback> callback) override
    {
        orion::log_info("CEF: beforeunload dialog auto-continued (leave-site prompts not supported yet)");
        callback->Continue(true, CefString());
        return true;
    }

    // CefPermissionHandler — getUserMedia / getDisplayMedia. Parks the
    // request and hands the decision to C#, which answers from the persisted
    // per-website setting or shows the consent prompt. Returning false uses
    // CEF's Alloy-style default (deny).
    bool OnRequestMediaAccessPermission(CefRefPtr<CefBrowser> browser,
                                        CefRefPtr<CefFrame> /*frame*/,
                                        const CefString& /*requesting_origin*/,
                                        uint32_t requested_permissions,
                                        CefRefPtr<CefMediaAccessCallback> callback) override
    {
        if (g_shutting_down) return false;
        if (!m_entry->callbacks.mediaAccessPermissionRequested) return false;

        // Decisions are keyed by the page (main frame) URL like orion-linux,
        // not the requesting frame's origin.
        std::wstring pageUrl = browser->GetMainFrame()->GetURL().ToWString();

        int64_t requestId = g_nextMediaRequestId++;
        g_pendingMediaRequests[requestId] = {callback, requested_permissions,
                                             browser->GetIdentifier()};
        m_entry->callbacks.mediaAccessPermissionRequested(
            pageUrl.c_str(), toWebMediaPermissions(requested_permissions), requestId);
        return true;
    }

    // CefFindHandler
    void OnFindResult(CefRefPtr<CefBrowser> /*browser*/,
                      int /*identifier*/, int count,
                      const CefRect& /*selectionRect*/,
                      int activeMatchOrdinal, bool finalUpdate) override
    {
        if (g_shutting_down) return;
        // Interim updates carry partial counts/ordinals while the renderer
        // is still scanning; report only the authoritative final one.
        if (!finalUpdate) return;
        if (m_entry->callbacks.findResult)
            m_entry->callbacks.findResult(count, activeMatchOrdinal);
    }

    // CefDisplayHandler
    void OnAddressChange(CefRefPtr<CefBrowser> /*browser*/,
                         CefRefPtr<CefFrame> frame,
                         const CefString& url) override
    {
        if (g_shutting_down) return;
        std::wstring wurl = url.ToWString();
        orion::log_debug("CEF: OnAddressChange — isMain=%d hasCb=%d",
            frame->IsMain() ? 1 : 0,
            m_entry->callbacks.activeURLChanged ? 1 : 0);
        if (frame->IsMain() && m_entry->callbacks.activeURLChanged) {
            // Don't update the address bar with internal URLs.
            if (wurl.starts_with(L"chrome-error://") || wurl.starts_with(L"data:"))
                return;
            m_entry->callbacks.activeURLChanged(wurl.c_str());
        }
    }

    void OnTitleChange(CefRefPtr<CefBrowser> /*browser*/,
                       const CefString& title) override
    {
        if (g_shutting_down) return;
        if (m_entry->errorPagePending) return;
        if (m_entry->callbacks.titleChanged) {
            std::wstring wtitle = title.ToWString();
            m_entry->callbacks.titleChanged(wtitle.c_str());
        }
    }

    // HTML5 element fullscreen. With Alloy style Blink grants the request
    // and sizes the content to the browser view on its own; this is a
    // notification, and the window/chrome transition happens in C#.
    void OnFullscreenModeChange(CefRefPtr<CefBrowser> /*browser*/,
                                bool fullscreen) override
    {
        if (g_shutting_down) return;
        m_entry->isElementFullscreen = fullscreen;
        if (m_entry->callbacks.elementFullscreenChanged)
            m_entry->callbacks.elementFullscreenChanged(fullscreen ? 1 : 0);
    }

    void OnFaviconURLChange(CefRefPtr<CefBrowser> browser,
                            const std::vector<CefString>& icon_urls) override
    {
        if (g_shutting_down) return;

        auto frame = browser->GetMainFrame();
        if (!m_entry->callbacks.faviconUrlChanged || icon_urls.empty() || !frame) return;

        std::wstring pageUrl = frame->GetURL().ToWString();
        std::wstring iconUrls;
        for (size_t i = 0; i < icon_urls.size(); i++) {
            if (i > 0) iconUrls += L'\n';
            iconUrls += icon_urls[i].ToWString();
        }
        m_entry->callbacks.faviconUrlChanged(pageUrl.c_str(), iconUrls.c_str());
    }

    bool OnConsoleMessage(CefRefPtr<CefBrowser> browser,
                          cef_log_severity_t /*level*/,
                          const CefString& message,
                          const CefString& /*source*/,
                          int /*line*/) override
    {
        static const std::string prefix = "__ORION_MSG__:";
        std::string msg = message.ToString();
        if (!msg.starts_with(prefix))
            return false;

        std::string rest = msg.substr(prefix.size());

        if (rest.starts_with("inputFocus:")) {
            std::string json = rest.substr(11);
            if (m_entry->callbacks.inputFocus)
                m_entry->callbacks.inputFocus(json.c_str());
        } else if (rest == "formSubmit") {
            scrapeCredentialsViaDevTools(browser);
        }
        return true;
    }

    void scrapeCredentialsViaDevTools(CefRefPtr<CefBrowser> browser)
    {
        if (!m_entry->callbacks.formSubmit) return;

        static std::string scrapeScript = orion::gtk::get_scrape_username_password_script();

        auto params = CefDictionaryValue::Create();
        params->SetString("expression", scrapeScript);
        params->SetBool("returnByValue", true);

        int msgId = m_entry->nextDevToolsMsgId++;
        browser->GetHost()->ExecuteDevToolsMethod(msgId, "Runtime.evaluate", params);
    }

    // CefLoadHandler
    void OnLoadingStateChange(CefRefPtr<CefBrowser> /*browser*/,
                              bool isLoading, bool canGoBack, bool canGoForward) override
    {
        if (g_shutting_down) return;
        if (m_entry->callbacks.loadingStateChanged) {
            m_entry->callbacks.loadingStateChanged(isLoading ? 1 : 0);
        }
        if (m_entry->callbacks.navigationStateChanged) {
            m_entry->callbacks.navigationStateChanged(canGoBack ? 1 : 0, canGoForward ? 1 : 0);
        }
    }

    void OnLoadStart(CefRefPtr<CefBrowser> /*browser*/,
                     CefRefPtr<CefFrame> frame,
                     CefLoadHandler::TransitionType) override
    {
        if (g_shutting_down) return;
        if (frame->IsMain() && m_entry->callbacks.progressChanged) {
            m_entry->callbacks.progressChanged(0.1);
        }
    }

    void OnLoadEnd(CefRefPtr<CefBrowser> browser,
                   CefRefPtr<CefFrame> frame, int) override
    {
        if (g_shutting_down) return;
        if (frame->IsMain()) {
            if (m_entry->errorPagePending) {
                // Only restore opacity for our data: URI page, not
                // for the chrome-error:// page that loads first.
                std::string url = frame->GetURL();
                if (url.starts_with("data:")) {
                    m_entry->errorPagePending = false;
                    HWND hwnd = browser->GetHost()->GetWindowHandle();
                    // Delay restoring opacity to let the GPU compositor
                    // render our error page before it becomes visible.
                    SetTimer(hwnd, reinterpret_cast<UINT_PTR>(hwnd), 500,
                        [](HWND h, UINT, UINT_PTR id, DWORD) {
                            KillTimer(h, id);
                            if (IsWindow(h))
                                SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
                        });
                }
            }
            if (m_entry->callbacks.progressChanged)
                m_entry->callbacks.progressChanged(1.0);

            injectAutofillScripts(frame);
        }
    }

    void OnLoadError(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     ErrorCode errorCode,
                     const CefString& errorText,
                     const CefString& failedUrl) override
    {
        if (g_shutting_down) return;
        if (!frame->IsMain()) return;
        if (errorCode == ERR_ABORTED) return;
        if (!m_entry->callbacks.navigationError) return;

        int32_t category = ErrorCategory::Unknown;
        switch (errorCode) {
        case ERR_NAME_NOT_RESOLVED:
            category = ErrorCategory::DnsError; break;
        case ERR_CONNECTION_TIMED_OUT:
        case ERR_TIMED_OUT:
            category = ErrorCategory::Timeout; break;
        case ERR_CONNECTION_REFUSED:
            category = ErrorCategory::ConnectionRefused; break;
        case ERR_SSL_PROTOCOL_ERROR:
        case ERR_CERT_COMMON_NAME_INVALID:
        case ERR_CERT_DATE_INVALID:
        case ERR_CERT_AUTHORITY_INVALID:
        case ERR_SSL_VERSION_OR_CIPHER_MISMATCH:
            category = ErrorCategory::SslError; break;
        default:
            category = ErrorCategory::NetworkError; break;
        }

        std::wstring wUrl = failedUrl.ToWString();
        std::wstring wText = errorText.ToWString();
        wchar_t* html = m_entry->callbacks.navigationError(
            category, static_cast<int32_t>(errorCode),
            wUrl.c_str(), wText.c_str());
        if (html) {
            std::string utf8Html = narrow(html);
            CoTaskMemFree(html);
            // Make the HWND fully transparent so chrome-error:// is never
            // visible, then load our error page as a data: URI.
            // OnLoadEnd restores opacity once the data: page has rendered.
            HWND hwnd = browser->GetHost()->GetWindowHandle();
            SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);
            m_entry->errorPagePending = true;
            std::string dataUri = "data:text/html;base64," +
                CefURIEncode(CefBase64Encode(utf8Html.data(), utf8Html.size()), false).ToString();
            frame->LoadURL(dataUri);
        }
    }

    // CefLifeSpanHandler
    bool OnBeforePopup(CefRefPtr<CefBrowser> /*browser*/,
                       CefRefPtr<CefFrame> /*frame*/,
                       int /*popup_id*/,
                       const CefString& target_url,
                       const CefString& /*target_frame_name*/,
                       CefLifeSpanHandler::WindowOpenDisposition target_disposition,
                       bool user_gesture,
                       const CefPopupFeatures& popupFeatures,
                       CefWindowInfo& windowInfo,
                       CefRefPtr<CefClient>& client,
                       CefBrowserSettings& settings,
                       CefRefPtr<CefDictionaryValue>& /*extra_info*/,
                       bool* /*no_javascript_access*/) override
    {
        if (g_shutting_down) return true;
        orion::log_debug("CEF: OnBeforePopup — disposition=%d isPopup=%d",
            (int)target_disposition, popupFeatures.isPopup);

        // No features → open as a new standalone tab or window (no opener
        // link), honoring the disposition Blink computed from the click's
        // modifiers. Cancel the popup so CEF doesn't create a linked
        // browser, which would deadlock on CloseBrowser during shutdown.
        if (!popupFeatures.isPopup) {
            if (auto cb = getOpenURLCallback()) {
                int32_t disposition = OpenDisposition::NewForegroundTab;
                toOpenDisposition(target_disposition, disposition);
                cb(m_entry->mainWindowHwnd, target_url.ToWString().c_str(), disposition);
            }
            return true;
        }

        CreatePopupCallback createCb = getCreatePopupCallback();
        if (!createCb) return true;

        int32_t w  = popupFeatures.widthSet  ? popupFeatures.width  : 800;
        int32_t h  = popupFeatures.heightSet ? popupFeatures.height : 600;
        int32_t px = popupFeatures.xSet      ? popupFeatures.x      : -1;
        int32_t py = popupFeatures.ySet      ? popupFeatures.y      : -1;

        HWND originatingHwnd = m_entry->mainWindowHwnd;

        PopupCreateResult result = createCb(w, h, px, py, 1, 1,
            1, user_gesture ? 1 : 0, originatingHwnd, target_url.ToWString().c_str());
        if (!result.parentHwnd) return true;

        auto popupEntry = std::make_shared<BrowserEntry>();
        popupEntry->callbacks = result.callbacks;
        popupEntry->popupParentHwnd = result.parentHwnd;

        windowInfo.SetAsChild(result.parentHwnd, CefRect(0, 0, 800, 600));
        client = new OrionCefClient(popupEntry);

        orion::log_info("CEF: OnBeforePopup — SetAsChild parentHwnd=%p", result.parentHwnd);
        return false;
    }

    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override
    {
        ++g_live_browser_count;
        m_entry->browser = browser;

        // Creation that completes after shutdown began (popup creation is
        // async): close immediately so the shutdown pump can drain to zero.
        if (g_shutting_down) {
            m_entry->closeInitiated = true;
            browser->GetHost()->CloseBrowser(true);
            DestroyWindow(browser->GetHost()->GetWindowHandle());
            return;
        }

        m_entry->devToolsRegistration =
            browser->GetHost()->AddDevToolsMessageObserver(
                new OrionDevToolsObserver(m_entry));

        applyColorSchemeOverride(browser);
        if (!m_entry->customUserAgent.empty())
            applyUserAgentOverride(*m_entry);

        if (m_entry->popupParentHwnd) {
            HWND hwnd = browser->GetHost()->GetWindowHandle();
            g_browsers[hwnd] = m_entry;
            orion::log_info("CEF: OnAfterCreated (popup) — hwnd=%p parent=%p",
                hwnd, m_entry->popupParentHwnd);
            if (auto readyCb = getPopupReadyCallback())
                readyCb(m_entry->popupParentHwnd, hwnd);
        }
    }

    bool DoClose(CefRefPtr<CefBrowser> /*browser*/) override
    {
        // Page-initiated close (window.close()) the page was allowed to
        // make: C# closes the hosting tab or popup window, and that close
        // path funnels back through destroyBrowserView, which completes the
        // browser's destruction.
        if (!m_entry->closeInitiated && m_entry->callbacks.closeRequested)
            m_entry->callbacks.closeRequested();
        // Returning false would make CEF post WM_CLOSE to the browser HWND's
        // top-level ancestor — the WinUI window (CloseHostWindow uses
        // GetAncestor(GA_ROOT)). Return true instead; the close call sites
        // (destroyBrowserView, cleanBrowserEngine) destroy the browser HWND,
        // which is what lets destruction proceed to OnBeforeClose.
        return true;
    }

    void OnBeforeClose(CefRefPtr<CefBrowser> /*browser*/) override
    {
        m_entry->browser = nullptr;
        --g_live_browser_count;
    }

    // CefKeyboardHandler — fires BEFORE CEF/page handle the key.
    // Esc during element fullscreen is handled by C# via reservedKeyEvent
    // (gated by prevent_escape_from_exiting_fullscreen); with Alloy style
    // exiting on Esc is the client's job, done via RequestExitElementFullscreen.
    bool OnPreKeyEvent(CefRefPtr<CefBrowser> /*browser*/,
                       const CefKeyEvent& event,
                       CefEventHandle /*os_event*/,
                       bool* /*is_keyboard_shortcut*/) override
    {
        return forwardReservedKey(*m_entry, event);
    }

    // Fallback for non-reserved keys the page didn't handle.
    bool OnKeyEvent(CefRefPtr<CefBrowser> /*browser*/,
                    const CefKeyEvent& event,
                    CefEventHandle /*os_event*/) override
    {
        return forwardUnhandledKey(*m_entry, event);
    }

    // CefRequestHandler — navigations the user directed away from the
    // current tab: middle-click, Ctrl+click, Shift+click on links.
    bool OnOpenURLFromTab(CefRefPtr<CefBrowser> /*browser*/,
                          CefRefPtr<CefFrame> /*frame*/,
                          const CefString& target_url,
                          CefRequestHandler::WindowOpenDisposition target_disposition,
                          bool /*user_gesture*/) override
    {
        if (g_shutting_down) return false;

        int32_t disposition;
        if (!toOpenDisposition(target_disposition, disposition))
            return false;
        auto cb = getOpenURLCallback();
        if (!cb)
            return false;

        orion::log_debug("CEF: OnOpenURLFromTab — disposition=%d", (int)target_disposition);
        cb(m_entry->mainWindowHwnd, target_url.ToWString().c_str(), disposition);
        return true;
    }

    // CefRequestHandler — HTTP auth challenge (401 / 407). Called on the IO
    // thread — the only BrowserCallbacks source that is: park the callback
    // and marshal to the UI thread, where all callbacks into C# must run.
    // C# shows a credentials dialog and resolves via RespondToAuthCredentials.
    bool GetAuthCredentials(CefRefPtr<CefBrowser> browser,
                            const CefString& /*origin_url*/,
                            bool isProxy,
                            const CefString& host,
                            int port,
                            const CefString& realm,
                            const CefString& /*scheme*/,
                            CefRefPtr<CefAuthCallback> callback) override
    {
        if (g_shutting_down || !m_entry->callbacks.authCredentialsRequested)
            return false;  // cancel: no UI to ask

        CefPostTask(TID_UI, base::BindOnce(
            [](std::shared_ptr<BrowserEntry> entry, CefRefPtr<CefAuthCallback> callback,
               int browserId, std::wstring host, int32_t port, bool isProxy,
               std::wstring realm)
            {
                // closeInitiated covers the gap between destroyBrowserView's
                // sweep and OnBeforeClose nulling entry->browser: a challenge
                // posted just before the close would otherwise park a fresh
                // entry and raise a dialog for the closed view.
                if (g_shutting_down || !entry->browser || entry->closeInitiated) {
                    callback->Cancel();
                    return;
                }
                int64_t requestId = g_nextAuthRequestId++;
                g_pendingAuthRequests[requestId] = { callback, browserId };
                orion::log_debug("CEF: auth challenge (proxy=%d) — request %lld",
                    isProxy ? 1 : 0, (long long)requestId);
                entry->callbacks.authCredentialsRequested(
                    host.c_str(), port, isProxy ? 1 : 0, realm.c_str(), requestId);
            },
            m_entry, callback, browser->GetIdentifier(),
            host.ToWString(), static_cast<int32_t>(port), isProxy, realm.ToWString()));
        return true;
    }

    // CefRequestHandler — render process crash notification
    void OnRenderProcessTerminated(CefRefPtr<CefBrowser> /*browser*/,
                                   TerminationStatus status,
                                   int /*error_code*/,
                                   const CefString& /*error_string*/) override
    {
        if (g_shutting_down) return;
        const char* reasonStr = "Unknown";
        switch (status) {
        case TS_ABNORMAL_TERMINATION: reasonStr = "AbnormalTermination"; break;
        case TS_PROCESS_WAS_KILLED:   reasonStr = "ProcessWasKilled"; break;
        case TS_PROCESS_CRASHED:      reasonStr = "ProcessCrashed"; break;
        case TS_PROCESS_OOM:          reasonStr = "OutOfMemory"; break;
        case TS_LAUNCH_FAILED:        reasonStr = "LaunchFailed"; break;
        }
        if (m_entry->callbacks.webProcessTerminated)
            m_entry->callbacks.webProcessTerminated("CEF Renderer", static_cast<uint32_t>(status), reasonStr);
    }

    CefRefPtr<CefResourceRequestHandler> GetResourceRequestHandler(
        CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
        CefRefPtr<CefRequest>, bool, bool,
        const CefString&, bool&) override
    {
        return new UserAgentRequestHandler(m_entry->customUserAgent);
    }


    // CefDownloadHandler
    bool OnBeforeDownload(CefRefPtr<CefBrowser> /*browser*/,
                          CefRefPtr<CefDownloadItem> download_item,
                          const CefString& suggested_name,
                          CefRefPtr<CefBeforeDownloadCallback> callback) override
    {
        auto originUrl = download_item->GetOriginalUrl().ToString();
        auto restart = bridgeConsumePendingRestart(originUrl);
        if (restart.downloadId > 0) {
            m_cefDownloadMap[download_item->GetId()] = restart.downloadId;
            callback->Continue(widen(restart.filePath), false);
            return true;
        }

        std::wstring folderPath;
        if (!bridgeGetDownloadFolder(m_entry->profileId, folderPath))
            return false;

        bool askUser = bridgeShouldAskDownloadLocation(m_entry->profileId);

        std::wstring filename = suggested_name.ToWString();
        auto destPath = folderPath + L"\\" + filename;
        if (!askUser) {
            for (int i = 1; i < 100; i++) {
                DWORD attrs = GetFileAttributesW(destPath.c_str());
                if (attrs == INVALID_FILE_ATTRIBUTES)
                    break;
                auto dotPos = filename.rfind(L'.');
                if (dotPos != std::wstring::npos && dotPos > 0)
                    destPath = folderPath + L"\\" + filename.substr(0, dotPos)
                        + L"(" + std::to_wstring(i) + L")"
                        + filename.substr(dotPos);
                else
                    destPath = folderPath + L"\\" + filename + L"(" + std::to_wstring(i) + L")";
            }
        }

        if (askUser) {
            callback->Continue(destPath, true);
            return true;
        }

        int64_t totalSize = download_item->GetTotalBytes();
        if (totalSize < 0) totalSize = 0;

        int64_t dbId = bridgeCreateDownload(
            m_entry->profileId, narrow(filename), narrow(destPath),
            download_item->GetOriginalUrl().ToString(), totalSize);
        if (dbId > 0)
            m_cefDownloadMap[download_item->GetId()] = dbId;

        callback->Continue(destPath, false);
        return true;
    }

    void OnDownloadUpdated(CefRefPtr<CefBrowser> /*browser*/,
                           CefRefPtr<CefDownloadItem> download_item,
                           CefRefPtr<CefDownloadItemCallback> callback) override
    {
        auto it = m_cefDownloadMap.find(download_item->GetId());
        if (it == m_cefDownloadMap.end()) {
            // "Ask" mode: DB entry deferred until user picks a location via CEF's save dialog.
            auto fullPath = download_item->GetFullPath().ToString();
            if (fullPath.empty())
                return;
            auto filename = fullPath;
            auto sep = filename.rfind('\\');
            if (sep != std::string::npos)
                filename = filename.substr(sep + 1);
            int64_t totalSize = download_item->GetTotalBytes();
            if (totalSize < 0) totalSize = 0;
            int64_t dbId = bridgeCreateDownload(
                m_entry->profileId, filename, fullPath,
                download_item->GetOriginalUrl().ToString(), totalSize);
            if (dbId <= 0)
                return;
            m_cefDownloadMap[download_item->GetId()] = dbId;
            it = m_cefDownloadMap.find(download_item->GetId());
        }
        int64_t dbId = it->second;

        if (download_item->IsComplete()) {
            bridgeUnregisterCancelHandler(dbId);
            int64_t finalSize = download_item->GetReceivedBytes();
            bridgeFinishDownload(dbId, finalSize);
            m_cefDownloadMap.erase(it);
        } else if (download_item->IsCanceled()) {
            bridgeUnregisterCancelHandler(dbId);
            m_cefDownloadMap.erase(it);
        } else if (download_item->IsInterrupted()) {
            bridgeUnregisterCancelHandler(dbId);
            bridgeFailDownload(dbId);
            m_cefDownloadMap.erase(it);
        } else if (download_item->IsInProgress()) {
            bridgeRegisterCancelHandler(dbId, [callback]() {
                callback->Cancel();
            });
            int64_t received = download_item->GetReceivedBytes();
            int64_t total = download_item->GetTotalBytes();
            if (total < 0) total = 0;
            bridgeUpdateDownloadProgress(dbId, received, total);
        }
    }

    // Context menu: the app draws its own menu (WinUI flyout). Snapshot the
    // params (CEF forbids keeping params/model refs past this call), ask C#
    // to show a menu, and cancel the CEF-side menu — commands run later via
    // executeContextMenuCommand, so nothing stays pending.
    bool RunContextMenu(CefRefPtr<CefBrowser> browser,
                        CefRefPtr<CefFrame> /*frame*/,
                        CefRefPtr<CefContextMenuParams> params,
                        CefRefPtr<CefMenuModel> /*model*/,
                        CefRefPtr<CefRunContextMenuCallback> callback) override
    {
        if (g_shutting_down) return false;
        if (!m_entry->callbacks.contextMenuRequested) return false;

        // The pass-through casts below rely on our wire enums matching CEF's.
        static_assert(WebContextMenuTypeFlagsPage == int(CM_TYPEFLAG_PAGE)
            && WebContextMenuTypeFlagsFrame == int(CM_TYPEFLAG_FRAME)
            && WebContextMenuTypeFlagsLink == int(CM_TYPEFLAG_LINK)
            && WebContextMenuTypeFlagsMedia == int(CM_TYPEFLAG_MEDIA)
            && WebContextMenuTypeFlagsSelection == int(CM_TYPEFLAG_SELECTION)
            && WebContextMenuTypeFlagsEditable == int(CM_TYPEFLAG_EDITABLE));
        static_assert(WebContextMenuEditFlagsCanUndo == int(CM_EDITFLAG_CAN_UNDO)
            && WebContextMenuEditFlagsCanRedo == int(CM_EDITFLAG_CAN_REDO)
            && WebContextMenuEditFlagsCanCut == int(CM_EDITFLAG_CAN_CUT)
            && WebContextMenuEditFlagsCanCopy == int(CM_EDITFLAG_CAN_COPY)
            && WebContextMenuEditFlagsCanPaste == int(CM_EDITFLAG_CAN_PASTE)
            && WebContextMenuEditFlagsCanDelete == int(CM_EDITFLAG_CAN_DELETE)
            && WebContextMenuEditFlagsCanSelectAll == int(CM_EDITFLAG_CAN_SELECT_ALL));
        static_assert(WebContextMenuMediaTypeImage == int(CM_MEDIATYPE_IMAGE)
            && WebContextMenuMediaTypeVideo == int(CM_MEDIATYPE_VIDEO)
            && WebContextMenuMediaTypeAudio == int(CM_MEDIATYPE_AUDIO)
            && WebContextMenuMediaTypeCanvas == int(CM_MEDIATYPE_CANVAS)
            && WebContextMenuMediaTypeFile == int(CM_MEDIATYPE_FILE)
            && WebContextMenuMediaTypePlugin == int(CM_MEDIATYPE_PLUGIN));

        // These strings back the request's borrowed pointers for the
        // duration of the callback.
        std::string linkUrl = params->GetLinkUrl().ToString();
        std::string unfilteredLinkUrl = params->GetUnfilteredLinkUrl().ToString();
        std::string sourceUrl = params->GetSourceUrl().ToString();
        std::string pageUrl = params->GetPageUrl().ToString();
        std::string selectionText = params->GetSelectionText().ToString();
        std::string linkText = params->GetTitleText().ToString();

        ContextMenuRequest req = {};
        req.x = params->GetXCoord();
        req.y = params->GetYCoord();
        req.typeFlags = uint32_t(params->GetTypeFlags());
        req.editFlags = uint32_t(params->GetEditStateFlags()) & 0x7F;  // only the bits we define
        req.mediaType = int32_t(params->GetMediaType());
        req.hasImageContents = params->HasImageContents() ? 1 : 0;
        req.canGoBack = browser->CanGoBack() ? 1 : 0;
        req.canGoForward = browser->CanGoForward() ? 1 : 0;
        req.canPrint = 1;
        req.linkUrl = linkUrl.c_str();
        req.unfilteredLinkUrl = unfilteredLinkUrl.c_str();
        req.sourceUrl = sourceUrl.c_str();
        req.pageUrl = pageUrl.c_str();
        req.selectionText = selectionText.c_str();
        req.linkText = linkText.c_str();

        if (!m_entry->callbacks.contextMenuRequested(&req))
            return false;

        callback->Cancel();
        return true;
    }

private:
    std::shared_ptr<BrowserEntry> m_entry;
    std::unordered_map<uint32_t, int64_t> m_cefDownloadMap;

    IMPLEMENT_REFCOUNTING(OrionCefClient);
};

// ---------------------------------------------------------------------------
// DevTools frontend browser client. CEF forces can_dock off for its
// browsers, so the frontend runs undocked-style — no dock or close controls
// of its own — inside a child HWND that C# hosts in its DevTools pane beside
// the inspected view. Lifetime is reported through the inspected view's
// devToolsViewChanged callback.
// ---------------------------------------------------------------------------

class OrionDevToolsClient : public CefClient,
                            public CefLifeSpanHandler,
                            public CefKeyboardHandler
{
public:
    explicit OrionDevToolsClient(std::shared_ptr<BrowserEntry> entry)
        : m_entry(std::move(entry))
    {}

    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefKeyboardHandler> GetKeyboardHandler() override { return this; }

    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override
    {
        ++g_live_browser_count;
        m_entry->devToolsBrowser = browser;
        applyColorSchemeOverride(browser);
        if (g_shutting_down) return;

        HWND hwnd = browser->GetHost()->GetWindowHandle();
        if (!hwnd) {
            orion::log_error("CEF: DevTools browser created without a window handle");
            return;
        }
        orion::log_info("CEF: DevTools created — hwnd=%p", hwnd);
        if (m_entry->callbacks.devToolsViewChanged)
            m_entry->callbacks.devToolsViewChanged(hwnd);
    }

    void OnBeforeClose(CefRefPtr<CefBrowser> /*browser*/) override
    {
        --g_live_browser_count;
        m_entry->devToolsBrowser = nullptr;
        // Closes that follow the inspected view's own teardown arrive after
        // C# has disposed that view and its callback delegates.
        if (g_shutting_down || m_entry->closeInitiated) return;
        if (m_entry->callbacks.devToolsViewChanged)
            m_entry->callbacks.devToolsViewChanged(nullptr);
    }

    bool OnPreKeyEvent(CefRefPtr<CefBrowser> /*browser*/,
                       const CefKeyEvent& event,
                       CefEventHandle /*os_event*/,
                       bool* /*is_keyboard_shortcut*/) override
    {
        return forwardReservedKey(*m_entry, event);
    }

    bool OnKeyEvent(CefRefPtr<CefBrowser> /*browser*/,
                    const CefKeyEvent& event,
                    CefEventHandle /*os_event*/) override
    {
        return forwardUnhandledKey(*m_entry, event);
    }

private:
    std::shared_ptr<BrowserEntry> m_entry;

    IMPLEMENT_REFCOUNTING(OrionDevToolsClient);
};

// ---------------------------------------------------------------------------
// CEF initialization.
// ---------------------------------------------------------------------------

// Directory of this module (the engine DLL, not the exe) — CEF resources
// (icudtl.dat, *.pak, locales\) ship next to the engine DLL.
static std::wstring engineModuleDir()
{
    return moduleDirFromAddress(reinterpret_cast<const void*>(&engineModuleDir));
}

static void ensureCefInitialized(int64_t profileId)
{
    if (g_cef_initialized) return;

    orion::log_info("CEF: Initializing (external message pump)...");

    CefMainArgs main_args(GetModuleHandle(nullptr));

    if (!g_sandbox_info) {
        orion::log_error("CEF: the Windows sandbox bootstrap is required");
        return;
    }

    CefSettings settings;
    settings.multi_threaded_message_loop = false;
    settings.external_message_pump = true;
    settings.no_sandbox = false;

    settings.command_line_args_disabled = true;
    settings.log_severity = LOGSEVERITY_DISABLE;

    // Chromium takes its process-singleton lock under root_cache_path. A
    // regular profile also uses that root for persistent site data. A private
    // process has its own temporary root for Chromium bookkeeping and uses a
    // separate, off-the-record request context for all of its views.
    wchar_t privateFlag[2] = {};
    const bool isPrivate = GetEnvironmentVariableW(L"ORION_PRIVATE_SESSION", privateFlag, 2) == 1
        && privateFlag[0] == L'1';
    if (profileId > 0) {
        std::wstring root = getProfileDataPath(profileId, EngineId::Blink).wstring();
        CefString(&settings.root_cache_path) = root;
        if (!isPrivate) {
            CefString(&settings.cache_path) = root;
        }
    } else {
        CefString(&settings.root_cache_path) =
            orion::data_directory_path("com.kagi.orion", {"blink"});
    }
    g_cef_profile_id = profileId;

    std::wstring engineDir = engineModuleDir();
    CefString(&settings.resources_dir_path) = engineDir;
    std::wstring localesDir = engineDir + L"\\locales";
    CefString(&settings.locales_dir_path) = localesDir;

    // Sec-CH-UA brand metadata is NOT derived from this string (it stays
    // Chromium-versioned); the per-view override and the request-handler
    // header strip keep client hints from contradicting the UA.
    if (!g_default_user_agent.empty())
        CefString(&settings.user_agent) = g_default_user_agent;

    if (!g_cef_app) g_cef_app = new OrionCefApp();
    g_cef_initialized = CefInitialize(main_args, settings, g_cef_app, g_sandbox_info);
    orion::log_info("CEF: CefInitialize %s", g_cef_initialized ? "succeeded" : "FAILED");

    // Kick-start the message pump — process any work queued during initialization.
    if (g_cef_initialized) {
        g_request_context = isPrivate
            ? CefRequestContext::CreateContext(CefRequestContextSettings(), nullptr)
            : CefRequestContext::GetGlobalContext();
        if (isPrivate && (!g_request_context || g_request_context->IsGlobal()
                           || !g_request_context->GetCachePath().empty())) {
            g_request_context = nullptr;
            orion::log_error("CEF: could not create an off-the-record request context");
        }
        applyEnginePreferences();
        CefDoMessageLoopWork();
    }
}

// ---------------------------------------------------------------------------
// Exported CEF pump API — called from C# via the watcher thread pattern.
// ---------------------------------------------------------------------------

HANDLE getCefWakeupEventHandle()
{
    if (!g_cef_wakeup_event)
        g_cef_wakeup_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    return g_cef_wakeup_event;
}

void pumpCefMessageLoop()
{
    if (g_cef_initialized)
        CefDoMessageLoopWork();
}

// ---------------------------------------------------------------------------
// browser_engine.h implementation
// ---------------------------------------------------------------------------

HWND createBrowserView(HWND parentHwnd, BrowserCallbacks callbacks, int64_t profileId, const char* initialUrl)
{
    const char* url = (initialUrl && *initialUrl) ? initialUrl : "about:blank";
    orion::log_debug("CEF: createBrowserView — parent=%p profileId=%lld", parentHwnd, profileId);

    ensureCefInitialized(profileId);
    if (!g_cef_initialized || !g_request_context) {
        orion::log_error("CEF: createBrowserView — browser context unavailable");
        return nullptr;
    }
    if (profileId != g_cef_profile_id) {
        orion::log_error("CEF: createBrowserView — profileId %lld, but this "
            "process's engine is bound to profile %lld (one profile per process)",
            profileId, g_cef_profile_id);
        return nullptr;
    }

    auto entry = std::make_shared<BrowserEntry>();
    entry->callbacks = callbacks;
    entry->mainWindowHwnd = parentHwnd;
    entry->profileId = profileId;

    CefWindowInfo windowInfo;
    windowInfo.SetAsChild(parentHwnd, CefRect(0, 0, 1, 1));
    windowInfo.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

    CefBrowserSettings browserSettings;
    CefRefPtr<OrionCefClient> client = new OrionCefClient(entry);

    auto browser = CefBrowserHost::CreateBrowserSync(
        windowInfo, client, url, browserSettings, nullptr, g_request_context);

    if (!browser) {
        orion::log_error("CEF: CreateBrowserSync failed");
        return nullptr;
    }

    entry->browser = browser;

    HWND browserHwnd = browser->GetHost()->GetWindowHandle();
    g_browsers[browserHwnd] = entry;
    orion::log_info("CEF: createBrowserView — hwnd=%p, browser=%p, frame=%p",
        browserHwnd, browser.get(),
        browser->GetMainFrame().get());

    return browserHwnd;
}

void respondToMediaAccessPermission(int64_t requestId, bool allow)
{
    auto node = g_pendingMediaRequests.extract(requestId);
    if (node.empty()) return; // view already gone, or resolved by shutdown
    PendingMediaAccessRequest& pending = node.mapped();
    if (allow)
        pending.callback->Continue(pending.requestedPermissions);
    else
        pending.callback->Cancel();
}

void respondToAuthCredentials(int64_t requestId, bool allow,
                              const wchar_t* username, const wchar_t* password)
{
    auto node = g_pendingAuthRequests.extract(requestId);
    if (node.empty()) return; // view already gone, or cancelled by shutdown
    PendingAuthRequest& pending = node.mapped();
    if (allow)
        pending.callback->Continue(username ? username : L"", password ? password : L"");
    else
        pending.callback->Cancel();
}

void destroyBrowserView(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it != g_browsers.end()) {
        if (it->second->browser) {
            // Dropping a pending request's callback ref makes CEF deny it.
            int browserId = it->second->browser->GetIdentifier();
            std::erase_if(g_pendingMediaRequests,
                [browserId](const auto& entry) { return entry.second.browserId == browserId; });
            std::erase_if(g_pendingAuthRequests,
                [browserId](auto& entry) {
                    if (entry.second.browserId != browserId)
                        return false;
                    entry.second.callback->Cancel();
                    return true;
                });
            it->second->closeInitiated = true;
            // The DevTools browser must reach OnBeforeClose too before
            // CefShutdown; close it explicitly rather than relying on the
            // inspected contents' destruction to cascade.
            it->second->browser->GetHost()->CloseDevTools();
            it->second->browser->GetHost()->CloseBrowser(true);
            // DoClose returns true (client-managed window): we must destroy
            // the browser HWND ourselves or destruction never reaches
            // OnBeforeClose.
            DestroyWindow(hwnd);
        }
        g_browsers.erase(it);
    }
}

void cleanBrowserEngine()
{
    g_shutting_down = true;

    // Denies (via the dropped callback refs) any media-access requests still
    // pending before the browsers close underneath them.
    g_pendingMediaRequests.clear();

    // Auth challenges want an explicit Cancel so their network requests fail
    // promptly rather than dangling into browser teardown.
    for (auto& [id, pending] : g_pendingAuthRequests)
        pending.callback->Cancel();
    g_pendingAuthRequests.clear();

    for (auto& [hwnd, entry] : g_browsers) {
        if (entry->browser) {
            entry->closeInitiated = true;
            entry->browser->GetHost()->CloseDevTools();
            entry->browser->GetHost()->CloseBrowser(true);
            // See destroyBrowserView: client-managed browser windows must be
            // destroyed by us for the close to reach OnBeforeClose.
            DestroyWindow(hwnd);
        }
    }

    if (g_cef_initialized) {
        // Pump until every browser completes OnBeforeClose — including
        // closes initiated earlier via destroyBrowserView that were never
        // pumped to completion. CefShutdown requires all browsers to be
        // destroyed first. Keep g_browsers alive during this — CEF may
        // still access browser state during the close sequence.
        while (g_live_browser_count > 0)
            CefDoMessageLoopWork();

        g_browsers.clear();

        // No CefDoMessageLoopWork after this point (pumpCefMessageLoop
        // self-gates on g_cef_initialized). CefShutdown drains remaining
        // tasks and tears down the Chrome profile, flushing cookies and
        // site data to disk.
        g_cef_initialized = false;
        g_request_context = nullptr;
        orion::log_info("CEF: all browsers closed; calling CefShutdown");
        CefShutdown();
        orion::log_info("CEF: CefShutdown complete");
    } else {
        g_browsers.clear();
    }

    if (g_cef_wakeup_event) {
        CloseHandle(g_cef_wakeup_event);
        g_cef_wakeup_event = nullptr;
    }
}

HRESULT loadURL(HWND hwnd, const std::string& url)
{
    orion::log_debug("CEF: loadURL — hwnd=%p", hwnd);
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end()) {
        orion::log_error("CEF: loadURL — hwnd not in g_browsers (size=%zu)", g_browsers.size());
        return E_HANDLE;
    }
    if (!it->second->browser) {
        orion::log_error("CEF: loadURL — entry->browser is null");
        return E_HANDLE;
    }

    CefRefPtr<CefFrame> frame = it->second->browser->GetMainFrame();
    if (!frame) {
        orion::log_error("CEF: loadURL — GetMainFrame() returned null");
        return E_FAIL;
    }

    orion::log_info("CEF: loadURL — calling frame->LoadURL");
    frame->LoadURL(url);
    return S_OK;
}

void loadHTMLString(HWND hwnd, const std::string& html, const std::string& unreachableUrl)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    CefRefPtr<CefFrame> frame = it->second->browser->GetMainFrame();
    if (!frame) return;
    std::string dataUri = "data:text/html;base64," +
        CefURIEncode(CefBase64Encode(html.data(), html.size()), false).ToString();
    frame->LoadURL(dataUri);
}

void requestExitElementFullscreen(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    if (it->second->isElementFullscreen)
        it->second->browser->GetHost()->ExitFullscreen(/*will_cause_resize=*/true);
}

void reload(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it != g_browsers.end() && it->second->browser)
        it->second->browser->Reload();
}

void reloadBypassingCache(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it != g_browsers.end() && it->second->browser)
        it->second->browser->ReloadIgnoreCache();
}

void forward(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it != g_browsers.end() && it->second->browser)
        it->second->browser->GoForward();
}

void backward(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it != g_browsers.end() && it->second->browser)
        it->second->browser->GoBack();
}

// Chrome's own settings write these profile preferences (the accessibility
// minimum font size, the JavaScript content default); CefBrowserSettings'
// equivalent fields are NOT honored under the Chrome bootstrap, while these
// apply immediately, open tabs included.
static void applyEnginePreferences()
{
    if (!g_cef_initialized)
        return;  // applied after CefInitialize instead
    CefRefPtr<CefRequestContext> context = g_request_context;
    if (!context)
        return;

    auto setPref = [&context](const char* pref, CefRefPtr<CefValue> value) {
        CefString error;
        if (!context->SetPreference(pref, value, error)) {
            orion::log_error("CEF: SetPreference(%s) failed: %s",
                pref, error.ToString().c_str());
        }
    };

    CefRefPtr<CefValue> fontSize = CefValue::Create();
    fontSize->SetInt(g_minimum_font_size);
    setPref("webkit.webprefs.minimum_font_size", fontSize);
    setPref("webkit.webprefs.minimum_logical_font_size", fontSize);

    CefRefPtr<CefValue> jsEnabled = CefValue::Create();
    jsEnabled->SetBool(g_javascript_enabled);
    setPref("webkit.webprefs.javascript_enabled", jsEnabled);

    // Empty URLs set the profile-default content setting. ALLOW stands
    // Chromium's own popup blocker down so every window.open reaches
    // OnBeforePopup and the app-side gesture gate decides.
    if (g_native_popup_blocking != -1) {
        context->SetContentSetting(CefString(), CefString(),
            CEF_CONTENT_SETTING_TYPE_POPUPS,
            g_native_popup_blocking == 0 ? CEF_CONTENT_SETTING_VALUE_ALLOW
                                         : CEF_CONTENT_SETTING_VALUE_DEFAULT);
    }

    orion::log_info(
        "CEF: engine preferences applied — minimum font size %d, javascript %d, native popup blocking %d",
        g_minimum_font_size, g_javascript_enabled ? 1 : 0, g_native_popup_blocking);
}

void setMinimumFontSize(int32_t size)
{
    g_minimum_font_size = size > 0 ? size : 0;
    applyEnginePreferences();
}

void setJavaScriptEnabled(bool enabled)
{
    g_javascript_enabled = enabled;
    applyEnginePreferences();
}

void setNativePopupBlocking(bool enabled)
{
    g_native_popup_blocking = enabled ? 1 : 0;
    applyEnginePreferences();
}

void setCustomUserAgent(HWND hwnd, const wchar_t* userAgent)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end()) return;
    it->second->customUserAgent = userAgent ? narrow(userAgent) : "";
    applyUserAgentOverride(*it->second);
}

void setDefaultUserAgent(const wchar_t* userAgent)
{
    g_default_user_agent = userAgent ? userAgent : L"";
    if (g_cef_initialized)
        orion::log_warning("CEF: setDefaultUserAgent after CefInitialize — "
                           "takes effect on next launch");
}

void startDownload(const char* url)
{
    for (auto& [hwnd, entry] : g_browsers) {
        if (entry->browser) {
            entry->browser->GetHost()->StartDownload(url);
            return;
        }
    }
}

void setUseDarkAppearance(bool useDarkAppearance)
{
    g_useDarkAppearance = useDarkAppearance;
    for (auto& [hwnd, entry] : g_browsers) {
        if (entry->browser)
            applyColorSchemeOverride(entry->browser);
        if (entry->devToolsBrowser)
            applyColorSchemeOverride(entry->devToolsBrowser);
    }
}

void executeJavaScript(HWND hwnd, const char* script)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    auto frame = it->second->browser->GetMainFrame();
    if (frame)
        frame->ExecuteJavaScript(script, frame->GetURL(), 0);
}

void findInPage(HWND hwnd, const char* text, bool forward, bool findNext)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    it->second->browser->GetHost()->Find(text, forward, /*matchCase=*/false, findNext);
}

void requestTabSnapshot(HWND hwnd, int32_t maxPixelWidth)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end())
        return;
    requestSnapshot(*it->second, maxPixelWidth);
}

void stopFindInPage(HWND hwnd, bool clearSelection)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    it->second->browser->GetHost()->StopFinding(clearSelection);
}

void executeContextMenuCommand(HWND hwnd, WebContextMenuCommand command, const char* arg, int32_t x, int32_t y)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    auto browser = it->second->browser;

    // Edit commands target the focused frame (the field the menu was for);
    // renderer focus survives the app menu taking Win32 focus.
    CefRefPtr<CefFrame> frame = browser->GetFocusedFrame();
    if (!frame) frame = browser->GetMainFrame();

    switch (command) {
    case WebContextMenuCommandBack:      browser->GoBack(); break;
    case WebContextMenuCommandForward:   browser->GoForward(); break;
    case WebContextMenuCommandReload:    browser->Reload(); break;
    case WebContextMenuCommandUndo:      if (frame) frame->Undo(); break;
    case WebContextMenuCommandRedo:      if (frame) frame->Redo(); break;
    case WebContextMenuCommandCut:       if (frame) frame->Cut(); break;
    case WebContextMenuCommandCopy:      if (frame) frame->Copy(); break;
    case WebContextMenuCommandPaste:     if (frame) frame->Paste(); break;
    case WebContextMenuCommandDelete:    if (frame) frame->Delete(); break;
    case WebContextMenuCommandSelectAll: if (frame) frame->SelectAll(); break;
    case WebContextMenuCommandPrint:     browser->GetHost()->Print(); break;
    case WebContextMenuCommandSaveUrl:
        if (arg && *arg)
            browser->GetHost()->StartDownload(arg);
        break;
    case WebContextMenuCommandInspectElement:
        // x/y are the request's coordinates — same unit space GetXCoord
        // produced, so they pass through unconverted.
        showDevTools(hwnd, x, y);
        break;
    }
}

void showDevTools(HWND hwnd, int32_t x, int32_t y)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    BrowserEntry& entry = *it->second;

    // Chrome style with a native parent is the only DevTools form CEF offers
    // outside Views hosting: a frameless child of the window that hosts the
    // inspected view, positioned by C# once OnAfterCreated reports it. The
    // window info is ignored when DevTools is already open — then this only
    // focuses it (and inspects the element).
    CefWindowInfo windowInfo;
    HWND parent = entry.popupParentHwnd ? entry.popupParentHwnd : entry.mainWindowHwnd;
    windowInfo.SetAsChild(parent, CefRect(0, 0, 1, 1));
    CefPoint inspectAt = x >= 0 && y >= 0 ? CefPoint(x, y) : CefPoint();
    entry.browser->GetHost()->ShowDevTools(windowInfo, new OrionDevToolsClient(it->second),
        CefBrowserSettings(), inspectAt);
}

void closeDevTools(HWND hwnd)
{
    auto it = g_browsers.find(hwnd);
    if (it == g_browsers.end() || !it->second->browser) return;
    it->second->browser->GetHost()->CloseDevTools();
}
