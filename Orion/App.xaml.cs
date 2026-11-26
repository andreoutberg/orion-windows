using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Orion
{
    /// <summary>
    /// Mirrors orion::settings::AppOpensWith from the core module.
    /// </summary>
    public enum AppOpensWith
    {
        NewWindow = 1,
        NewPrivateWindow = 2,
        AllWindowsFromLastSession = 3,
        AllNonPrivateWindowsFromLastSession = 4,
    }

    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary> 
    public partial class App : Application
    {
        private Window? _window;
        private volatile bool _coreRunning;
        private Thread? _coreWatcherThread;
        private ManualResetEvent? _stopEvent;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // Initialize DLL search paths before any P/Invoke calls
            OrionBackend.Initialize();
            OrionBackend.SetupLogging();
        }

#if ORION_BLINK_ONLY
        internal const string DefaultBrowserEngine = "blink";
        internal static bool SupportsWebKit => false;
#else
        internal const string DefaultBrowserEngine = "webkit";
        internal static bool SupportsWebKit => true;
#endif

        /// <summary>
        /// Both the launcher and the window use the same engine selection.
        /// An explicit --engine argument overrides the profile registry.
        /// </summary>
        internal static string RequestedEngine(Profile? profile, string[] args)
        {
            string? engineArg = args.FirstOrDefault(a => a.StartsWith("--engine=", StringComparison.Ordinal))?["--engine=".Length..];
            string requested = (engineArg ?? profile?.Engine ?? DefaultBrowserEngine).Trim().ToLowerInvariant();
            if (requested is not "webkit" and not "blink")
            {
                OrionBackend.LogWarning($"Unknown browser engine '{requested}' — using '{DefaultBrowserEngine}'");
                requested = DefaultBrowserEngine;
            }
            return requested;
        }

        private bool TryLoadBrowserEngine(string requested)
        {
            if (!SupportsWebKit && requested == "webkit")
            {
                ShowFatalStartupErrorAndExit("WebKit is unavailable",
                    "This build uses Chromium. Start Orion with --engine=blink to open this profile.");
                return false;
            }
            try
            {
                OrionBackend.LoadBrowserEngine(requested);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                OrionBackend.LogError(ex.Message);
                ShowFatalStartupErrorAndExit("Orion failed to start",
                    $"{ex.Message}\n\nReinstall Orion to repair the browser engine files.");
                return false;
            }
        }

        /// <summary>
        /// Shows a fatal startup error and exits when it's dismissed. Runs
        /// before MainWindow exists, so it hosts the dialog in a bare window
        /// of its own.
        /// </summary>
        private void ShowFatalStartupErrorAndExit(string title, string message)
        {
            var root = new Grid();
            _window = new Window { Title = "Orion", Content = root };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 320));
            root.Loaded += async (_, _) =>
            {
                _ = await PopupDialog.ShowMessageAsync(root, title, message);
                Exit();
            };
            _window.Activate();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        /// <summary>
        /// Registry id of the profile this process hosts, resolved by
        /// Program.Main before the single-instance gate (0 = default profile,
        /// e.g. in packaged builds where the XAML-generated Main runs).
        /// </summary>
        internal static long StartupProfileId;

        /// <summary>
        /// Other profiles to reopen on a cold start — the profiles that were
        /// open at the last Quit Orion, minus the one this process hosts.
        /// Filled by Program.Main from the consumed reopen_profiles app
        /// setting; empty in packaged builds (XAML-generated Main).
        /// </summary>
        internal static readonly List<long> ReopenProfileIds = [];

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // One profile per process: the registry (common orion.db) is
            // readable before core init and is the authority on which
            // profiles exist, their engines, and the default.
            string coreResult = OrionBackend.InitOrionCore(StartupProfileId);
            if (coreResult != "OK")
                throw new InvalidOperationException($"Failed to initialize Orion core: {coreResult}");

            List<Profile> allProfiles = OrionBackend.GetProfiles();
            Profile currentProfile =
                allProfiles.FirstOrDefault(p => p.Id == OrionBackend.GetCurrentProfileId())
                ?? allProfiles.First(p => p.IsDefault);

            // Engine selection: this profile's registry engine;
            // --engine=webkit|blink overrides. The plugin must be loaded
            // before the watcher thread reads GetCefWakeupEventHandle and
            // before the first window.
            if (!TryLoadBrowserEngine(RequestedEngine(currentProfile, Environment.GetCommandLineArgs())))
                return;

            OrionBackend.SetupDownloadCallbacks();
            if (!PrivateBrowsing.IsActive)
                NotificationService.Initialize(DispatcherQueue.GetForCurrentThread(), currentProfile.Id);
            ErrorPageProvider.Initialize();
            ThemeService.Initialize(currentProfile.Id);
            EnginePreferences.Initialize(currentProfile.Id);
            EntityShortcuts.Refresh();
            AltMenuBeepSuppressor.Initialize();

#if ORION_TEST_EXTENSIONS
            // The API test extensions exercise the WebKit extension runtime;
            // the CEF port has none yet, so a blink session would only churn
            // the profile DB and stall webview creation waiting on the
            // extension list (ConfigureWebViewExtensions pumps until loaded).
            if (!OrionBackend.IsBlinkEngine)
                ReinstallTestExtensions();
#endif

            // Drive both the orion-linux run loop and CEF message pump from a
            // background watcher thread. The thread blocks on auto-reset events:
            //   1. Core wakeup — signalled by RunLoop::wakeup() when background
            //      threads post work to the main loop.
            //   2. CEF wakeup (Blink only) — signalled by OnScheduleMessagePumpWork()
            //      when CEF has tasks to process.
            // When either event fires (or the fallback timeout expires), both pumps
            // are ticked on the UI thread via DispatcherQueue.TryEnqueue.
            // WebKit returns null for the CEF event, so only include it when present
            // to avoid WAIT_FAILED from WaitForMultipleObjects with an invalid handle.
            Win32.HANDLE coreWakeupEvent = OrionBackend.GetWakeupEventHandle();
            Win32.HANDLE cefWakeupEvent = OrionBackend.GetCefWakeupEventHandle();
            bool hasCefEvent = cefWakeupEvent.Value != IntPtr.Zero;
            _stopEvent = new ManualResetEvent(false);
            IntPtr[] waitHandles = hasCefEvent
                ? [coreWakeupEvent.Value, cefWakeupEvent.Value, _stopEvent.SafeWaitHandle.DangerousGetHandle()]
                : [coreWakeupEvent.Value, _stopEvent.SafeWaitHandle.DangerousGetHandle()];
            uint handleCount = (uint)waitHandles.Length;
            uint timeout = hasCefEvent ? 16u : 100u;
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _coreRunning = true;
            _coreWatcherThread = new Thread(() =>
            {
                while (_coreRunning)
                {
                    _ = Win32.WaitForMultipleObjects(handleCount, waitHandles, false, timeout);
                    if (!_coreRunning) break;
                    _ = dispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_coreRunning) return;
                        OrionBackend.PumpCefMessageLoop();
                        if (!OrionBackend.TickOrionCore())
                            _coreRunning = false;
                    });
                }
            })
            { IsBackground = true, Name = "OrionCoreWatcher" };
            _coreWatcherThread.Start();

            // Register popup-window (window.open) callbacks.  Native calls
            // into C# synchronously on the UI thread when a page triggers
            // window.open; we materialise a PopupWindow and hand back its
            // host HWND + callbacks struct, then adopt the WKView HWND once
            // native has created it.  Popup tabs are NOT persisted to the
            // db and do NOT contribute to history.
            OrionBackend.SetCreatePopupCallback(OnCreatePopup);
            OrionBackend.SetPopupReadyCallback(OnPopupReady);
            OrionBackend.SetOpenURLCallback(OnOpenURL);

            // Startup behavior is an app-wide setting (common orion.db).
            var appOpensWith = (AppOpensWith)int.Parse(OrionBackend.GetAppSetting(
                "settings::general::app_opens_with",
                ((int)AppOpensWith.AllNonPrivateWindowsFromLastSession).ToString()));

            // Clean up stale backend windows that shouldn't be restored.
            // This handles the case where the previous session didn't clean up
            // (e.g. crash), mirroring GTK's cleanup_backend_windows on open.
            // One profile per process: only this profile's windows are ours.
            MainWindow.CleanupBackendWindows(currentProfile.Id, appOpensWith);

            // Each unsaved group (is_tab_group=true) with has_ui_window=true
            // represents a distinct window from the previous session.
            // Saved groups are loaded into each window by LoadSavedTabGroups().
            if (appOpensWith is AppOpensWith.AllWindowsFromLastSession
                             or AppOpensWith.AllNonPrivateWindowsFromLastSession)
            {
                _window = RestoreProfileWindows(currentProfile);
            }

            // Always ensure at least one window exists
            _window ??= new MainWindow(currentProfile);
            _window.Activate();

            // Reopen the other profiles that were open at the last Quit Orion
            // (recorded by the quit broadcast, consumed by Program.Main). Each
            // gets its own process; a duplicate spawn is a no-op thanks to the
            // per-profile single-instance gate. Spawned after our window is up
            // so the focused profile wins the foreground.
            foreach (long id in ReopenProfileIds)
            {
                if (id != currentProfile.Id)
                    _ = System.Diagnostics.Process.Start(Program.LauncherPath, $"--profile={id}");
            }

            // Serve "open" requests forwarded by later launches of this
            // profile (single instance per profile): focus the existing
            // window rather than opening another one. "quit" arrives when
            // another profile's process confirmed Quit Orion — the user
            // already answered once, so exit without a second prompt.
            SingleInstance.StartServer(currentProfile.Id, dispatcherQueue,
                () =>
                {
                    MainWindow.FocusOrOpenForProfile(currentProfile);
                    // A self-deleted profile's successor spawn reaches an
                    // already-running process as a forwarded open, not a
                    // fresh launch — run the cleanup net here too.
                    ProfileDataCleanup.Run();
                },
                () => OnProfileDeleted(currentProfile.Id),
                OnProfilesChangedByOtherProcess,
                MainWindow.ExitAll,
                PrivateBrowsing.IsActive ? null : NotificationService.HandleForwardedActivation);

            // Remove leftover data dirs of deleted profiles (their rows mark
            // the ids as permanently burned; a dir survives deletion when its
            // process was still running at the time).
            ProfileDataCleanup.Run();

            // Launched by a click on one of our toasts while not running?
            if (!PrivateBrowsing.IsActive)
                NotificationService.HandleLaunchActivation();
        }

        /// <summary>
        /// Raised on the UI thread after another Orion process changed the
        /// profile registry (create/update/delete/reorder/default); profile
        /// UIs (settings page) subscribe to re-read GetProfiles.
        /// </summary>
        internal static event Action? ProfilesChangedRemotely;

        private static void OnProfilesChangedByOtherProcess()
        {
            EntityShortcuts.Refresh();
            ProfilesChangedRemotely?.Invoke();
        }

        private static void OnProfileDeleted(long profileId)
        {
            // Just exit; this process cannot remove its own data dirs — the
            // deleter (or the next launch) does it once we are fully gone.
            OrionBackend.LogInfo($"App: profile {profileId} was deleted; closing all windows");
            MainWindow.ExitAll();
        }

        /// <summary>
        /// Restores this process's profile windows. Each unsaved tab group
        /// with has_ui_window=true becomes its own window. Returns the first
        /// window created, or null if none.
        /// </summary>
        private static MainWindow? RestoreProfileWindows(Profile profile)
        {
            var toRestore = new List<(Profile profile, OrionBackend.PersistedTabGroup group)>();
            foreach (OrionBackend.PersistedTabGroup g in OrionBackend.GetTabGroups(profile.Id)
                .Where(g => g.IsTabGroup && g.HasUiWindow))
            {
                toRestore.Add((profile, g));
            }

            if (toRestore.Count == 0)
                return null;

            // Create one window synchronously for immediate UI feedback.
            (Profile firstProfile, OrionBackend.PersistedTabGroup firstGroup) = toRestore[0];
            var firstWindow = new MainWindow(firstProfile, [firstGroup]);
            toRestore.RemoveAt(0);

            // Remaining windows are created one per dispatcher tick so WinUI
            // can finish HWND setup between each — creating them all in a
            // tight loop races SetTitleBar against HWND registration.
            if (toRestore.Count > 0)
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                RestoreNextWindow(dispatcher, toRestore, 0);
            }

            return firstWindow;
        }

        private static void RestoreNextWindow(DispatcherQueue dispatcher,
            List<(Profile profile, OrionBackend.PersistedTabGroup group)> items, int index)
        {
            _ = dispatcher.TryEnqueue(() =>
            {
                (Profile profile, OrionBackend.PersistedTabGroup group) = items[index];
                var window = new MainWindow(profile, [group]);
                window.Activate();
                if (index + 1 < items.Count)
                    RestoreNextWindow(dispatcher, items, index + 1);
            });
        }

#if ORION_TEST_EXTENSIONS
        private static void ReinstallTestExtensions()
        {
            // One profile per process — install into the profile we host.
            long profileId = OrionBackend.GetCurrentProfileId();

            string exeDir = AppContext.BaseDirectory;
            string firefoxXpi = System.IO.Path.Combine(exeDir, "orion-api-test.xpi");
            string chromeCrx = System.IO.Path.Combine(exeDir, "orion-api-test.crx");

            bool hasFirefox = System.IO.File.Exists(firefoxXpi);
            bool hasChrome = System.IO.File.Exists(chromeCrx);
            if (!hasFirefox)
                LogWarning($"orion-api-test: package not found, skipping install: {firefoxXpi}");
            if (!hasChrome)
                LogWarning($"orion-api-test: package not found, skipping install: {chromeCrx}");
            if (!hasFirefox && !hasChrome)
                return;

            List<ExtensionEntry> installed = OrionBackend.GetInstalledExtensions(profileId);
            foreach (ExtensionEntry ext in installed)
            {
                if (ext.Name == "Orion API Test")
                    OrionBackend.UninstallExtension(ext.Id);
            }

            bool firefoxInstalled = false;
            if (hasFirefox)
            {
                string id = OrionBackend.InstallExtension(
                    profileId,
                    name: "orion-api-test-firefox",
                    displayName: "Orion API Test",
                    info: "WebExtension API test suite (Firefox)",
                    installURL: "",
                    webExtType: "firefox",
                    // Distinct webExtID per variant: the sku is derived from it, and
                    // the core resource cache is keyed by sku — two installs with
                    // empty ids collide on the fallback sku "0" and end up sharing
                    // one (wrong) resources bundle.
                    webExtID: "orion-api-test-firefox",
                    geckoId: "",
                    iconURL: "",
                    version: "1.0.0",
                    orionHosted: false,
                    filePath: firefoxXpi);
                firefoxInstalled = !string.IsNullOrEmpty(id);
                if (!firefoxInstalled)
                    LogWarning($"orion-api-test: firefox install failed: {firefoxXpi}");
            }

            bool chromeInstalled = false;
            if (hasChrome)
            {
                string id = OrionBackend.InstallExtension(
                    profileId,
                    name: "orion-api-test-chrome",
                    displayName: "Orion API Test",
                    info: "WebExtension API test suite (Chrome)",
                    installURL: "",
                    webExtType: "chrome",
                    webExtID: "orion-api-test-chrome",
                    geckoId: "",
                    iconURL: "",
                    version: "1.0.0",
                    orionHosted: false,
                    filePath: chromeCrx);
                chromeInstalled = !string.IsNullOrEmpty(id);
                if (!chromeInstalled)
                    LogWarning($"orion-api-test: chrome install failed: {chromeCrx}");
            }

            // The extension id is a fresh random UUID on every install (and we
            // reinstall on every debug launch), so log the popup URLs to make
            // the test page reachable without digging through the database.
            bool sawFirefox = false;
            bool sawChrome = false;
            foreach (ExtensionEntry ext in OrionBackend.GetInstalledExtensions(profileId))
            {
                if (ext.Name != "Orion API Test")
                    continue;
                bool isChrome = ext.ExtensionType == 2;
                string scheme = isChrome ? "chrome-extension" : "moz-extension";
                LogInfo($"orion-api-test ({ext.TypeLabel}): {scheme}://{ext.Id}/popup.html");
                if (isChrome)
                    sawChrome = true;
                else
                    sawFirefox = true;
            }
            if (firefoxInstalled && !sawFirefox)
                LogWarning("orion-api-test: firefox install reported success but the extension is missing from GetInstalledExtensions");
            if (chromeInstalled && !sawChrome)
                LogWarning("orion-api-test: chrome install reported success but the extension is missing from GetInstalledExtensions");
        }
#endif

        /// <summary>
        /// Stops the core watcher thread so no more work is enqueued on the
        /// dispatcher after the window closes. Must be called before
        /// ShutdownOrionCore().
        /// </summary>
        internal void StopCoreWatcher()
        {
            _coreRunning = false;
            _ = (_stopEvent?.Set());
            _coreWatcherThread?.Join();
            _stopEvent?.Dispose();
        }

        // Maps a pending HWND → either PopupWindowContent or (MainWindowContent, BrowserTab)
        // so that OnPopupReady can finish wiring the WKView HWND.
        private readonly Dictionary<nint, PopupWindowContent> _pendingPopups = [];
        private readonly Dictionary<nint, (MainWindowContent content, BrowserTab tab)> _pendingNewTabs = [];

        private MainWindowContent? FindMainWindowByHwnd(Win32.HWND hwnd)
        {
            foreach (MainWindow w in MainWindow.AllWindows)
            {
                Win32.HWND windowHwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                if (windowHwnd == hwnd)
                    return w.MainWindowContent;
            }
            return null;
        }

        private OrionBackend.PopupCreateResult OnCreatePopup(
            int requestedWidth, int requestedHeight,
            int requestedX, int requestedY,
            sbyte wantLocationBar, sbyte wantResizable,
            sbyte wantsPopup, sbyte userGesture,
            Win32.HWND originatingHwnd,
            string targetUrl)
        {
            try
            {
                LogDebug($"OnCreatePopup: wantsPopup={wantsPopup} userGesture={userGesture} " +
                         $"targetUrl='{targetUrl}' size={requestedWidth}x{requestedHeight} " +
                         $"originating={originatingHwnd}");

                // Pop-up blocking (Allow=1, Block=2, BlockAndNotify=3): like
                // Safari/Chrome, "block" means unsolicited popups — a
                // window.open backed by a user gesture always passes. A
                // never-set setting applies no gating.
                if (userGesture == 0 && EnginePreferences.PopupWindowsMode is 2 or 3)
                {
                    OrionBackend.LogInfo(
                        $"OnCreatePopup: blocked pop-up without user gesture to '{targetUrl}'");
                    if (EnginePreferences.PopupWindowsMode == 3
                        && FindMainWindowByHwnd(originatingHwnd) is { } blockedContent)
                    {
                        // Called from inside an engine callback — defer the
                        // banner UI to the next dispatcher tick.
                        string blockedUrl = targetUrl;
                        _ = blockedContent.DispatcherQueue.TryEnqueue(
                            () => blockedContent.ShowPopupBlockedBanner(blockedUrl));
                    }
                    return new OrionBackend.PopupCreateResult { ParentHwnd = Win32.HWND.Zero };
                }

                // window.open without features → open as new tab in the originating window
                if (wantsPopup == 0)
                    return CreateNewTabResult(originatingHwnd);

                return CreatePopupResult(requestedWidth, requestedHeight, wantLocationBar, originatingHwnd);
            }
            catch (Exception ex)
            {
                LogError($"OnCreatePopup failed: {ex.Message}");
                return new OrionBackend.PopupCreateResult { ParentHwnd = Win32.HWND.Zero };
            }
        }

        private OrionBackend.PopupCreateResult CreatePopupResult(
            int requestedWidth, int requestedHeight, sbyte wantLocationBar, Win32.HWND originatingHwnd)
        {
            int w = requestedWidth > 0 ? requestedWidth : 800;
            int h = requestedHeight > 0 ? requestedHeight : 600;
            bool locationBar = wantLocationBar != 0;

            var popup = new PopupWindow(w, h, locationBar);
            popup.Content_.OriginatingMainHwnd = originatingHwnd;
            popup.Closed += (_, _) => popup.Content_.Cleanup();
            popup.Activate();

            (Win32.HWND parentHwnd, OrionBackend.BrowserCallbacks callbacks) =
                popup.Content_.BuildForNative();
            _pendingPopups[parentHwnd.Value] = popup.Content_;
            popup.Closed += (_, _) => _pendingPopups.Remove(parentHwnd.Value);

            return new OrionBackend.PopupCreateResult
            {
                ParentHwnd = parentHwnd,
                Callbacks = callbacks,
            };
        }

        private OrionBackend.PopupCreateResult CreateNewTabResult(Win32.HWND originatingHwnd)
        {
            MainWindowContent? mainContent = FindMainWindowByHwnd(originatingHwnd);
            if (mainContent == null)
            {
                LogError($"CreateNewTabResult: no MainWindow for HWND {originatingHwnd}");
                return new OrionBackend.PopupCreateResult { ParentHwnd = Win32.HWND.Zero };
            }

            (Win32.HWND parentHwnd, OrionBackend.BrowserCallbacks callbacks, BrowserTab tab)? result = mainContent.CreateNewTabForWindowOpen();
            if (result == null)
            {
                LogError("CreateNewTabResult: CreateNewTabForWindowOpen returned null");
                return new OrionBackend.PopupCreateResult { ParentHwnd = Win32.HWND.Zero };
            }

            (Win32.HWND parentHwnd, OrionBackend.BrowserCallbacks callbacks, BrowserTab? tab) = result.Value;
            _pendingNewTabs[parentHwnd.Value] = (mainContent, tab);

            return new OrionBackend.PopupCreateResult
            {
                ParentHwnd = parentHwnd,
                Callbacks = callbacks,
            };
        }

        private void OnPopupReady(Win32.HWND parentHwnd, Win32.HWND webViewHwnd)
        {
            if (_pendingPopups.TryGetValue(parentHwnd.Value, out PopupWindowContent? popupContent))
            {
                _ = _pendingPopups.Remove(parentHwnd.Value);
                popupContent.AttachNativeWebView(webViewHwnd);

                // When called synchronously from native code (e.g. CEF's
                // OnBeforePopup), the popup's HwndHostPanel.Loaded hasn't
                // fired yet, so UpdatePosition silently fails (XamlRoot /
                // TransformToVisual not ready).  Re-trigger after layout.
                _ = popupContent.DispatcherQueue.TryEnqueue(
                    DispatcherQueuePriority.Low,
                    popupContent.DeferredResizeWebView);
            }
            else if (_pendingNewTabs.TryGetValue(parentHwnd.Value, out (MainWindowContent content, BrowserTab tab) tabEntry))
            {
                _ = _pendingNewTabs.Remove(parentHwnd.Value);
                tabEntry.content.AttachNewTabWebView(tabEntry.tab, webViewHwnd);
            }
            else
            {
                LogError($"OnPopupReady: no pending popup or tab for parentHwnd={parentHwnd}");
            }
        }

        private void OnOpenURL(Win32.HWND originatingHwnd, string targetUrl, int disposition)
        {
            MainWindowContent? mainContent = FindMainWindowByHwnd(originatingHwnd);
            if (mainContent == null)
            {
                LogError($"OnOpenURL: no MainWindow for HWND {originatingHwnd}");
                return;
            }
            mainContent.OpenLink(targetUrl, (OrionBackend.OpenDisposition)disposition);
        }

        /// <summary>
        /// Opens a URL in the MainWindow identified by originatingHwnd —
        /// popup windows route their context-menu link items through this.
        /// </summary>
        internal void OpenUrlFrom(Win32.HWND originatingHwnd, string url, OrionBackend.OpenDisposition disposition) =>
            OnOpenURL(originatingHwnd, url, (int)disposition);
    }
}
