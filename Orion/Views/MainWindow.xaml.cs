using Microsoft.UI.Xaml;
using System.Collections.Generic;
using System.Linq;

namespace Orion
{
    /// <summary>
    /// Thin shell window. All UI content lives in MainWindowContent (UserControl)
    /// which enables x:Bind. This class handles Window-level APIs only.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private static readonly List<MainWindow> _allWindows = [];
        internal static IReadOnlyList<MainWindow> AllWindows => _allWindows;
        private static bool _isExiting;

        /// <summary>
        /// Staging fields for the next MainWindowContent construction.
        /// Set before InitializeComponent so MainWindowContent can read them during its constructor
        /// (XAML-instantiated controls can't take constructor parameters).
        /// </summary>
        internal static Profile? PendingProfile;
        internal static List<OrionBackend.PersistedTabGroup>? PendingTabGroups;

        internal MainWindowContent MainWindowContent => MainContent;

        /// <summary>
        /// Opens a new window for the given profile, restoring its last session
        /// from the database if one exists.
        /// </summary>
        /// <summary>
        /// Brings the profile's existing window to the foreground, opening
        /// one only when none exists. For another profile this spawns (or,
        /// via its single-instance gate, focuses) that profile's process.
        /// Use this for profile switching; use OpenForProfile when a new
        /// window is explicitly wanted (Ctrl+N).
        /// </summary>
        internal static void FocusOrOpenForProfile(Profile profile)
        {
            if (profile.Id != OrionBackend.GetCurrentProfileId())
            {
                _ = OpenForProfile(profile);  // other process; its gate focuses
                return;
            }
            MainWindow? existing = _allWindows.Count > 0 ? _allWindows[^1] : null;
            if (existing != null)
                existing.ActivateAndFocus();
            else
                _ = OpenForProfile(profile);
        }

        /// <summary>
        /// Activates the window and asks Windows for the foreground — plain
        /// Activate() is not enough when the request originates from another
        /// process (the single-instance pipe), even with the sender's
        /// AllowSetForegroundWindow grant.
        /// </summary>
        internal void ActivateAndFocus()
        {
            Activate();
            _ = Win32.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }

        internal static MainWindow? OpenForProfile(Profile profile)
        {
            // One profile per process: another profile's windows live in that
            // profile's own process (its single-instance gate forwards to an
            // already-running one).
            if (profile.Id != OrionBackend.GetCurrentProfileId())
            {
                _ = System.Diagnostics.Process.Start(Program.LauncherPath, $"--profile={profile.Id}");
                return null;
            }

            List<OrionBackend.PersistedTabGroup>? tabGroups = null;
            var persisted = OrionBackend.GetTabGroups(profile.Id)
                .Where(g => g.IsTabGroup && !g.HasUiWindow).ToList();
            if (persisted.Count > 0)
                tabGroups = persisted;
            var window = new MainWindow(profile, tabGroups);
            window.Activate();
            return window;
        }

        public MainWindow(Profile? profile = null,
            List<OrionBackend.PersistedTabGroup>? tabGroups = null)
        {
            PendingProfile = profile;
            PendingTabGroups = tabGroups;
            InitializeComponent(); // Creates MainContent, which reads pending fields
            PendingProfile = null;
            PendingTabGroups = null;

            // Give the content a reference to this Window for native interop
            MainContent.ParentWindow = this;

            // Set up custom title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MainContent.TitleBarElement);
            Utils.SetOrionWindowIcon(this);
            AltMenuBeepSuppressor.Attach(this);

            _allWindows.Add(this);
            Closed += OnWindowClosed;
        }

        /// <summary>
        /// Closes all windows, preserving every profile's session for restore.
        /// </summary>
        internal static void ExitAll()
        {
            _isExiting = true;
            LibraryWindow.CloseIfOpen();
            PopularExtensionsWindow.CloseIfOpen();
            ResetWindow.CloseIfOpen();
            // Close all windows; iterate a copy because Close() mutates the list.
            foreach (MainWindow w in _allWindows.ToList())
                w.Close();
        }

        private void OnWindowClosed(object sender, WindowEventArgs e)
        {
            // X-closing the last window quits this profile's process, so it
            // takes the same confirmation path as Ctrl+Q; RequestExitAll
            // re-closes via ExitAll (_isExiting) on confirm. Scoped to this
            // profile only — other profiles' processes keep their windows.
            // Mid-session closes of other windows don't warn.
            if (!_isExiting && _allWindows.Count == 1
                && OrionBackend.GetBoolSetting(MainContent.ViewModel.ActiveProfileId,
                    "settings::general::show_warning_before_quitting", "settings::general",
                    "show_warning_before_quitting", true))
            {
                e.Handled = true;
                MainContent.RequestExitAll(quitAllProfiles: false);
                return;
            }

            _ = _allWindows.Remove(this);

            if (_isExiting || _allWindows.Count == 0)
            {
                // Full app exit or last window — preserve session for restore.
                MainContent.Cleanup();
            }
            else
            {
                // Mid-session close — close_window() handles each group:
                //   unsaved (is_tab_group=true)  → deleted from DB
                //   saved   (is_tab_group=false) → preserved with has_ui_window=false
                // Ensure saved groups have is_tab_group=false before calling
                // close_window, in case they were saved before this flag was used.
                foreach (TabGroupViewModel vm in MainContent.ViewModel.TabGroups)
                {
                    if (vm.Model.IsSaved)
                        OrionBackend.UpdateTabGroupIsTabGroup(vm.Model.Id, false);
                    OrionBackend.DeleteTabGroup(vm.Model.Id);
                }
                MainContent.Cleanup();
            }

            if (_allWindows.Count == 0)
            {
                // Clean up this profile's backend windows based on
                // app_opens_with, mirroring GTK's cleanup_backend_windows.
                CleanupBackendWindowsForCurrentProfile();

                // Release the profile's single-instance claim first: a launch
                // racing our shutdown should start a fresh process rather than
                // forward "open" to one that is tearing down.
                SingleInstance.Stop();

                // Cleanup() above already requested close on all browser
                // HWNDs; ShutdownOrionCore's cleanBrowserEngine() pumps CEF
                // until those closes complete, then calls CefShutdown().
                ((App)Application.Current).StopCoreWatcher();
                OrionBackend.ShutdownOrionCore();
                // Drain the run loop so g_core_api is destroyed and SQLite is closed.
                while (OrionBackend.TickOrionCore()) { }
            }
        }

        /// <summary>
        /// Cleans up this profile's backend window state per the app-wide
        /// startup setting so the next launch behaves correctly. One profile
        /// per process — other profiles' processes clean up after themselves.
        /// Mirrors orion-linux's cleanup_backend_windows.
        /// </summary>
        private static void CleanupBackendWindowsForCurrentProfile()
        {
            var appOpensWith = (AppOpensWith)int.Parse(OrionBackend.GetAppSetting(
                "settings::general::app_opens_with",
                ((int)AppOpensWith.AllNonPrivateWindowsFromLastSession).ToString()));
            CleanupBackendWindows(OrionBackend.GetCurrentProfileId(), appOpensWith);
        }

        /// <summary>
        /// Cleans up persisted windows for a profile based on the startup setting:
        ///   new_window / new_private_window → delete unsaved tab groups, reset has_ui_window on saved
        ///   all_non_private_windows         → delete private windows, reset has_ui_window on non-private
        ///   all_windows                     → no cleanup (preserve everything)
        /// </summary>
        internal static void CleanupBackendWindows(long profileId, AppOpensWith appOpensWith)
        {
            if (appOpensWith == AppOpensWith.AllWindowsFromLastSession)
                return;

            foreach (OrionBackend.PersistedTabGroup window in OrionBackend.GetTabGroups(profileId))
            {
                if (appOpensWith is AppOpensWith.NewWindow or AppOpensWith.NewPrivateWindow)
                {
                    if (window.IsTabGroup)
                        OrionBackend.DeleteTabGroup(window.Id);
                    else if (window.HasUiWindow)
                        OrionBackend.UpdateTabGroupHasUiWindow(window.Id, false);
                }
                else if (appOpensWith == AppOpensWith.AllNonPrivateWindowsFromLastSession)
                {
                    if (window.IsPrivate)
                        OrionBackend.DeleteTabGroup(window.Id);
                    else if (!window.IsTabGroup && window.HasUiWindow)
                        OrionBackend.UpdateTabGroupHasUiWindow(window.Id, false);
                }
            }
        }
    }
}
