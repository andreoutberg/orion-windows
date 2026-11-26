using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.System;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private class ShortcutEntry
        {
            public required VirtualKey Key { get; set; }
            public required VirtualKeyModifiers Modifiers { get; set; }
            /// <summary>User-facing action name, e.g. in shortcut-conflict messages.</summary>
            public required string DisplayName { get; init; }
            public string? KeyName { get; init; }
            public Action? Action { get; init; }
            /// <summary>
            /// Evaluated per keypress; false leaves the key unhandled so it
            /// keeps routing (to a focused text box, the page, ...). Null = always on.
            /// </summary>
            public Func<bool>? IsEnabled { get; init; }
            /// <summary>
            /// Reserved shortcuts are intercepted before the engine processes them,
            /// so web pages cannot override them (e.g. Ctrl+T, Ctrl+W, Ctrl+N).
            /// </summary>
            public bool IsReserved { get; init; }
            public KeyboardAccelerator? Accelerator { get; set; }
        }

        // VirtualKey doesn't define named members for OEM keys;
        // add our own naming which follows Win32 headers (WinUser.h)
        private const VirtualKey VK_OEM_COMMA = (VirtualKey)0xBC;

        // Source of truth: name → shortcut entry (for display and rebinding).
        // Entries with Action == null are display-only (ShortcutText for menus).
        private readonly Dictionary<string, ShortcutEntry> Shortcuts = [];

        // Dispatch index: key combo → shortcut entry (for O(1) key event lookup).
        // Built from Shortcuts; rebuilt on rebind.
        private readonly Dictionary<(VirtualKey, VirtualKeyModifiers), ShortcutEntry> ShortcutsByKeyCombo = [];

        private string ShortcutText(string name)
        {
            ShortcutEntry entry = Shortcuts[name];
            var sb = new StringBuilder();

            if (entry.Modifiers.HasFlag(VirtualKeyModifiers.Control))
                _ = sb.Append("Ctrl+");

            if (entry.Modifiers.HasFlag(VirtualKeyModifiers.Menu))
                _ = sb.Append("Alt+");

            if (entry.Modifiers.HasFlag(VirtualKeyModifiers.Shift))
                _ = sb.Append("Shift+");

            // we can't reliably use the Windows key as a modifier, but include it in the text for consistency if it's set
            if (entry.Modifiers.HasFlag(VirtualKeyModifiers.Windows))
                _ = sb.Append("Win+");

            _ = sb.Append(entry.KeyName ?? entry.Key.ToString());
            return sb.ToString();
        }

        private void RegisterKeyboardAccelerators()
        {
            Shortcuts["NewTab"] = new() { Key = VirtualKey.T, Modifiers = VirtualKeyModifiers.Control, DisplayName = "New Tab", IsReserved = true, Action = () => CreateNewTab(ViewModel.ResolveNewTabUrl()) };
            Shortcuts["CloseTab"] = new() { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Close Tab", IsReserved = true, Action = () => { BrowserTab? tab = ViewModel.ActiveTab; if (tab != null) CloseTab(tab); } };
            Shortcuts["AddBookmark"] = new() { Key = VirtualKey.D, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Add Bookmark", Action = () => _ = ShowAddBookmarkDialog() };
            Shortcuts["NewWindow"] = new() { Key = VirtualKey.N, Modifiers = VirtualKeyModifiers.Control, DisplayName = "New Window", IsReserved = true, Action = () => MainWindow.OpenForProfile(ViewModel.ActiveProfile) };
            Shortcuts["ShowHistory"] = new() { Key = VirtualKey.Y, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Show History", Action = () => LibraryWindow.ShowHistory(profileId: ViewModel.ActiveProfileId) };
            Shortcuts["Settings"] = new() { Key = VK_OEM_COMMA, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Settings", KeyName = ",", Action = () => OpenSettingsTab() };
            Shortcuts["SavePage"] = new() { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Save Page", Action = () => _ = ShowSavePageDialog() };
            // ShowSidebar in Gtk: Ctrl+Super+S. Ctrl+B is more or less "standard" for sidebars on Windows
            Shortcuts["ShowSidebar"] = new() { Key = VirtualKey.B, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Toggle Sidebar", Action = () => ViewModel.ShowSidebar = !ViewModel.ShowSidebar };
            Shortcuts["FocusAddressBar"] = new() { Key = VirtualKey.L, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Open Location", Action = () => _ = FocusAddressBar() };
            Shortcuts["SearchTabs"] = new() { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Search Open Tabs", Action = ShowTabSearch };
            // Page-first like Chromium and Firefox (web apps may bind Ctrl+R);
            // F5, Ctrl+F5 and Shift+F5 are the Windows-native equivalents.
            void reload() => ViewModel.ActiveTabViewModel?.ReloadCommand.Execute(null);
            void reloadBypassingCache() => ViewModel.ActiveTabViewModel?.ReloadBypassingCacheCommand.Execute(null);
            Shortcuts["Reload"] = new() { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Reload This Page", Action = reload };
            Shortcuts["ReloadF5"] = new() { Key = VirtualKey.F5, Modifiers = VirtualKeyModifiers.None, DisplayName = "Reload This Page", Action = reload };
            Shortcuts["ReloadBypassingCache"] = new() { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, DisplayName = "Reload Page Without Cache", Action = reloadBypassingCache };
            Shortcuts["ReloadBypassingCacheCtrlF5"] = new() { Key = VirtualKey.F5, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Reload Page Without Cache", Action = reloadBypassingCache };
            Shortcuts["ReloadBypassingCacheShiftF5"] = new() { Key = VirtualKey.F5, Modifiers = VirtualKeyModifiers.Shift, DisplayName = "Reload Page Without Cache", Action = reloadBypassingCache };
            // Find keys are not reserved: the page gets them first (Google
            // Docs-style overrides work), matching macOS/gtk and Chromium's
            // own reserved set (tab/window management only).
            Shortcuts["Find"] = new() { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Find", Action = OpenFindBar };
            Shortcuts["FindNext"] = new() { Key = VirtualKey.G, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Find Next", Action = () => FindNext(forward: true) };
            Shortcuts["FindPrevious"] = new() { Key = VirtualKey.G, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, DisplayName = "Find Previous", Action = () => FindNext(forward: false) };
            // F3/Shift+F3: the Windows-native find keys (Chrome/Edge/Firefox parity)
            Shortcuts["FindNextF3"] = new() { Key = VirtualKey.F3, Modifiers = VirtualKeyModifiers.None, DisplayName = "Find Next", Action = () => FindNext(forward: true) };
            Shortcuts["FindPreviousF3"] = new() { Key = VirtualKey.F3, Modifiers = VirtualKeyModifiers.Shift, DisplayName = "Find Previous", Action = () => FindNext(forward: false) };
            // Page-first like Chromium, whose reserved set excludes the DevTools keys.
            Shortcuts["ToggleDevTools"] = new() { Key = VirtualKey.F12, Modifiers = VirtualKeyModifiers.None, DisplayName = "Show Web Inspector", Action = ToggleDevTools };
            Shortcuts["ToggleDevToolsCtrlShiftI"] = new() { Key = VirtualKey.I, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, DisplayName = "Show Web Inspector", Action = ToggleDevTools };
            Shortcuts["ShowDownloads"] = new() { Key = VirtualKey.J, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Show Downloads", Action = OpenDownloadsTab };
            Shortcuts["NextTab"] = new() { Key = VirtualKey.Tab, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Next Tab", IsReserved = true, Action = () => SwitchToAdjacentTab(forward: true) };
            Shortcuts["PreviousTab"] = new() { Key = VirtualKey.Tab, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, DisplayName = "Previous Tab", IsReserved = true, Action = () => SwitchToAdjacentTab(forward: false) };

            // Reserved like Chrome/Firefox's Ctrl+1..9 tab switching
            for (int n = 1; n <= 9; n++)
            {
                int num = n;
                Shortcuts[$"CtrlNumber{num}"] = new()
                {
                    Key = VirtualKey.Number0 + num,
                    Modifiers = VirtualKeyModifiers.Control,
                    DisplayName = $"Switch to Tab {num}",
                    IsReserved = true,
                    Action = () => HandleCtrlNumberShortcut(num),
                };
            }

            Shortcuts["UndoCloseTab"] = new() { Key = VirtualKey.T, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, DisplayName = "Reopen Last Closed Tab", IsReserved = true, Action = () => _ = ViewModel.UndoCloseTab() };

            // Safari/Orion Mac use Shift+Cmd+\, but OEM keys move across
            // keyboard layouts (e.g. Finnish has no backslash key) — use a
            // letter combo instead.
            Shortcuts["TabOverview"] = new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, DisplayName = "Tab Overview", IsReserved = true, Action = ToggleTabOverview };

            // Gated by settings::tabs::use_ctrl_z_to_undo_close_tab (gtk binds
            // the same accel behind it; read per keypress like the Ctrl+1-9
            // toggle). Page-first so page text fields keep their undo, and
            // declined while a XAML text box has focus for the same reason.
            Shortcuts["UndoCloseTabCtrlZ"] = new()
            {
                Key = VirtualKey.Z,
                Modifiers = VirtualKeyModifiers.Control,
                DisplayName = "Reopen Last Closed Tab",
                IsEnabled = () => OrionBackend.GetBoolSetting(ViewModel.ActiveProfileId,
                        "settings::tabs::use_ctrl_z_to_undo_close_tab", "settings::tabs",
                        "use_ctrl_z_to_undo_close_tab", true)
                    && FocusManager.GetFocusedElement(XamlRoot) is not Microsoft.UI.Xaml.Controls.TextBox,
                Action = () => _ = ViewModel.UndoCloseTab(),
            };

            Shortcuts["Exit"] = new() { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control, DisplayName = "Quit Orion", IsReserved = true, Action = () => DispatcherQueue.TryEnqueue(() => RequestExitAll(quitAllProfiles: true)) };

            Shortcuts["ToggleFullscreen"] = new() { Key = VirtualKey.F11, Modifiers = VirtualKeyModifiers.None, DisplayName = "Toggle Full Screen", IsReserved = true, Action = ToggleFullscreen };

            SidebarPage[] sidebarPages = Enum.GetValues<SidebarPage>()[1..];
            for (int i = 0; i < sidebarPages.Length; i++)
            {
                SidebarPage page = sidebarPages[i];
                Shortcuts[$"Sidebar{page}"] = new()
                {
                    Key = VirtualKey.Number1 + i,
                    Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                    DisplayName = $"Show {page.DisplayName()} Sidebar",
                    Action = () => { ViewModel.ShowSidebar = true; SetSidebarPage(page); },
                };
            }

            // TODO: Reserved shortcuts still to implement (Chrome & Firefox reserve these):
            // Ctrl+Shift+W  — Close window (Chrome & Firefox)
            // Ctrl+Shift+N  — New private window (Chrome); Firefox uses Ctrl+Shift+P
            // Ctrl+F4       — Close tab (Chrome, alternative binding)

            // Build dispatch index and register XAML KeyboardAccelerators.
            foreach (ShortcutEntry entry in Shortcuts.Values)
            {
                if (entry.Action == null)
                    continue;

                ShortcutsByKeyCombo[(entry.Key, entry.Modifiers)] = entry;
                // Feed the recorder conflict check; identical across windows.
                EntityShortcuts.RegisterBuiltin(new KeyCombo(entry.Key, entry.Modifiers), entry.DisplayName);

                var accel = new KeyboardAccelerator { Key = entry.Key, Modifiers = entry.Modifiers };
                accel.Invoked += (s, e) =>
                {
                    // While a recorder captures, or when the entry's gate says
                    // off, decline (Handled stays false) so the keystroke
                    // keeps routing — to the recorder, a text box, the page.
                    if (ShortcutRecorderBox.AnyCapturing || entry.IsEnabled?.Invoke() == false)
                        return;
                    entry.Action();
                    e.Handled = true;
                };
                RootGrid.KeyboardAccelerators.Add(accel);
                entry.Accelerator = accel;
            }

            EntityShortcuts.Changed += RebuildEntityAccelerators;
            RebuildEntityAccelerators();
        }

        // Chrome-focus accelerators for entity shortcuts (tab groups /
        // profiles); web-view focus reaches them through OnEngineUnhandledKey
        // → DispatchShortcut instead. Rebuilt whenever an entity shortcut
        // changes; combos taken by built-in shortcuts are skipped (built-ins win).
        private readonly List<KeyboardAccelerator> _entityAccelerators = [];

        internal void RebuildEntityAccelerators()
        {
            foreach (KeyboardAccelerator accel in _entityAccelerators)
                _ = RootGrid.KeyboardAccelerators.Remove(accel);
            _entityAccelerators.Clear();

            foreach (KeyCombo combo in EntityShortcuts.CandidateCombos(ViewModel))
            {
                if (ShortcutsByKeyCombo.ContainsKey((combo.Key, combo.Modifiers)))
                    continue;
                var accel = new KeyboardAccelerator { Key = combo.Key, Modifiers = combo.Modifiers };
                accel.Invoked += (s, e) =>
                {
                    if (ShortcutRecorderBox.AnyCapturing)
                        return;
                    e.Handled = EntityShortcuts.Dispatch(combo, ViewModel);
                };
                RootGrid.KeyboardAccelerators.Add(accel);
                _entityAccelerators.Add(accel);
            }
        }

        /// <summary>
        /// Dispatches a key combo through the shortcut table.
        /// If reservedOnly is true, only reserved shortcuts are matched.
        /// Returns true if a matching shortcut was found and executed.
        /// </summary>
        private bool DispatchShortcut(VirtualKey vk, VirtualKeyModifiers modifiers, bool reservedOnly = false)
        {
            if (ShortcutRecorderBox.AnyCapturing)
                return false;
            if (ShortcutsByKeyCombo.TryGetValue((vk, modifiers), out ShortcutEntry? entry)
                && entry.Action != null
                && (!reservedOnly || entry.IsReserved)
                && entry.IsEnabled?.Invoke() != false)
            {
                entry.Action();
                return true;
            }
            // Entity shortcuts (tab groups / profiles) are page-first, never reserved.
            return !reservedOnly && EntityShortcuts.Dispatch(new KeyCombo(vk, modifiers), ViewModel);
        }

        /// <summary>
        /// Called before a key event reaches the engine.
        /// If the key matches a reserved shortcut, executes the action and returns true
        /// so the engine never sees the event (web pages cannot override it).
        /// </summary>
        internal bool OnEngineReservedKey(VirtualKey vk, VirtualKeyModifiers modifiers)
        {
            // WebKit's own Esc-exits-fullscreen handling never fires on the
            // Windows port (the web process only reacts to KeyDown events and
            // this port delivers RawKeyDown), so exit element fullscreen from
            // the UI process — mirroring WebKitGTK's WebKitWebViewBase. Both
            // engines route here (CEF via OnPreKeyEvent → reservedKeyEvent).
            // With prevent_escape_from_exiting_fullscreen on, Esc stays with
            // the page; F11 remains the reserved exit.
            if (vk == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None && _elementFullscreenWebView != null
                && !GetPreventEscapeFromExitingFullscreen())
            {
                _elementFullscreenWebView.RequestExitElementFullscreen();
                return true;
            }
            if (vk == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None && FindBar.IsOpen)
            {
                CloseFindBar();
                return true;
            }
            return DispatchShortcut(vk, modifiers, reservedOnly: true);
        }

        /// <summary>
        /// Called by the engine when the page did not handle a key event.
        /// Dispatches to the shared shortcut action table.
        /// </summary>
        internal bool OnEngineUnhandledKey(VirtualKey vk, VirtualKeyModifiers modifiers) => DispatchShortcut(vk, modifiers);
    }
}
