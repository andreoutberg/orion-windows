using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        /// <summary>
        /// Builds the main hamburger menu flyout.
        /// </summary>
        private void BuildMainMenu()
        {
            var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

            var switchWorkspace = new MenuFlyoutItem
            {
                Text = "Switch Workspace",
                Icon = new FontIcon { Glyph = "\uE8B7" },
            };
            bool showWorkspaceSwitcher = false;
            switchWorkspace.Click += (_, _) => showWorkspaceSwitcher = true;
            menu.Opening += (_, _) => switchWorkspace.Visibility = _sidebarPresentation == SidebarPresentation.Hidden
                ? Visibility.Visible : Visibility.Collapsed;
            menu.Closed += (_, _) =>
            {
                if (!showWorkspaceSwitcher)
                    return;
                showWorkspaceSwitcher = false;
                ShowTabGroupSwitcher(MenuButton);
            };
            menu.Items.Add(switchWorkspace);

            var newTab = new MenuFlyoutItem
            {
                Text = "New Tab",
                Icon = new FontIcon { Glyph = "\uE710" },
                KeyboardAcceleratorTextOverride = ShortcutText("NewTab"),
            };
            newTab.Click += NewTab_Click;
            menu.Items.Add(newTab);

            var downloads = new MenuFlyoutItem
            {
                Text = "Downloads",
                Icon = new FontIcon { Glyph = "\uE896" },
                KeyboardAcceleratorTextOverride = ShortcutText("ShowDownloads"),
            };
            downloads.Click += DownloadsButton_Click;
            menu.Items.Add(downloads);
            menu.Items.Add(new MenuFlyoutSeparator());

            // --- Orion ---
            var orion = new MenuFlyoutSubItem { Text = "Orion" };
            orion.Items.Add(new MenuFlyoutItem { Text = "About Orion..." });
            ((MenuFlyoutItem)orion.Items[^1]).Click += About_Click;
            orion.Items.Add(Disabled(new MenuFlyoutItem { Text = "Get Orion+" }));
            orion.Items.Add(new MenuFlyoutSeparator());
            orion.Items.Add(new MenuFlyoutItem { Text = "Popular Extensions..." });
            ((MenuFlyoutItem)orion.Items[^1]).Click += PopularExtensions_Click;
            var manageExtensions = new MenuFlyoutItem { Text = "Manage Extensions..." };
            manageExtensions.Click += ExtensionsButton_Click;
            orion.Items.Add(manageExtensions);
            orion.Items.Add(Disabled(new MenuFlyoutItem { Text = "Check for Updates..." }));
            orion.Items.Add(new MenuFlyoutSeparator());
            orion.Items.Add(new MenuFlyoutItem { Text = "Settings...", KeyboardAcceleratorTextOverride = ShortcutText("Settings") });
            ((MenuFlyoutItem)orion.Items[^1]).Click += Settings_Click;
            orion.Items.Add(Disabled(new MenuFlyoutItem { Text = "Settings for This Website...", KeyboardAcceleratorTextOverride = "Ctrl+Alt+," }));
            orion.Items.Add(new MenuFlyoutSeparator());
            orion.Items.Add(new MenuFlyoutItem { Text = "Reset Orion...", KeyboardAcceleratorTextOverride = "Shift+Ctrl+Backspace" });
            ((MenuFlyoutItem)orion.Items[^1]).Click += (s, e) => ResetWindow.Show();
            var services = new MenuFlyoutSubItem { Text = "Services" };
            orion.Items.Add(services);
            orion.Items.Add(new MenuFlyoutSeparator());
            // not sure if we will be able to implement these window-management related things on windows
            orion.Items.Add(Disabled(new MenuFlyoutItem { Text = "Hide Orion", KeyboardAcceleratorTextOverride = "Ctrl+H" }));
            orion.Items.Add(Disabled(new MenuFlyoutItem { Text = "Hide Others", KeyboardAcceleratorTextOverride = "Ctrl+Alt+H" }));
            orion.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show All" }));
            orion.Items.Add(new MenuFlyoutSeparator());
            orion.Items.Add(new MenuFlyoutItem { Text = "Quit Orion", KeyboardAcceleratorTextOverride = ShortcutText("Exit") });
            ((MenuFlyoutItem)orion.Items[^1]).Click += (s, e) => RequestExitAll(quitAllProfiles: true);
            menu.Items.Add(orion);

            // --- File ---
            var file = new MenuFlyoutSubItem { Text = "File" };
            // --- File/Profiles ---
            var profiles = new MenuFlyoutSubItem { Text = "Profiles" };
            MenuFlyoutSubItem activeProfile = new() { Text = ViewModel.ActiveProfileName };
            activeProfile.Items.Add(Disabled(new MenuFlyoutItem { Text = "Edit..." }));
            activeProfile.Items.Add(Disabled(new MenuFlyoutItem { Text = "Delete" }));
            activeProfile.Items.Add(new MenuFlyoutSeparator());
            var windows = new MenuFlyoutSubItem { Text = "Windows" };
            foreach (TabGroupViewModel groupVm in ViewModel.TabGroups)
            {
                TabGroup group = groupVm.Model;
                var item = new ToggleMenuFlyoutItem
                {
                    Text = group.DisplayName,
                    IsChecked = group == ViewModel.ActiveGroup?.Model,
                    KeyboardAcceleratorTextOverride = group.KeyboardShortcutDisplay,
                };
                item.Click += (s, e) => ViewModel.SelectTabGroup(group);
                windows.Items.Add(item);
            }
            activeProfile.Items.Add(windows);
            activeProfile.Items.Add(new MenuFlyoutSeparator());
            activeProfile.Items.Add(Disabled(new MenuFlyoutItem { Text = "Set Default" }));
            activeProfile.Items.Add(new MenuFlyoutSeparator());
            activeProfile.Items.Add(new MenuFlyoutItem { Text = "Take Snapshot" });
            ((MenuFlyoutItem)activeProfile.Items[^1]).Click += (s, e) => OrionBackend.CreateSnapshot(ViewModel.ActiveProfileId);
            activeProfile.Items.Add(new MenuFlyoutItem { Text = "Restore Snapshot..." });
            ((MenuFlyoutItem)activeProfile.Items[^1]).Click += RestoreSnapshot_Click;
            // Profile color/emoji picker
            activeProfile.Items.Add(new MenuFlyoutSeparator());
            var colorPickerItem = new MenuFlyoutItem
            {
                Style = (Style)Resources["ProfileColorPickerStyle"]
            };
            activeProfile.Items.Add(colorPickerItem);
            profiles.Items.Add(activeProfile);
            profiles.Items.Add(new MenuFlyoutSeparator());
            // TODO: add profile selection list
            profiles.Items.Add(Disabled(new MenuFlyoutItem { Text = "Some Other Placeholder Profile" }));
            profiles.Items.Add(new MenuFlyoutSeparator());
            profiles.Items.Add(Disabled(new MenuFlyoutItem { Text = "New Profile..." }));
            profiles.Items.Add(Disabled(new MenuFlyoutItem { Text = "Manage Profiles..." }));
            profiles.Items.Add(new MenuFlyoutSeparator());
            profiles.Items.Add(new RadioMenuFlyoutItem { Text = "Show Icon Only", GroupName = "ProfileShowPreference" });
            ((RadioMenuFlyoutItem)profiles.Items[^1]).IsChecked = true;
            profiles.Items.Add(new RadioMenuFlyoutItem { Text = "Show Name Only", GroupName = "ProfileShowPreference" });
            profiles.Items.Add(new RadioMenuFlyoutItem { Text = "Show Icon and Name", GroupName = "ProfileShowPreference" });
            file.Items.Add(profiles);

            file.Items.Add(new MenuFlyoutSeparator());
            file.Items.Add(new MenuFlyoutItem { Text = "New Tab", KeyboardAcceleratorTextOverride = ShortcutText("NewTab") });
            ((MenuFlyoutItem)file.Items[^1]).Click += NewTab_Click;
            file.Items.Add(new MenuFlyoutItem { Text = "New Window", KeyboardAcceleratorTextOverride = ShortcutText("NewWindow") });
            ((MenuFlyoutItem)file.Items[^1]).Click += NewWindow_Click;
            file.Items.Add(CreatePrivateWindowMenuItem());
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "New Focus Window", KeyboardAcceleratorTextOverride = "Ctrl+U" }));
            file.Items.Add(new MenuFlyoutSeparator());
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open File...", KeyboardAcceleratorTextOverride = "Ctrl+O" }));
            file.Items.Add(new MenuFlyoutItem { Text = "Open Location...", KeyboardAcceleratorTextOverride = ShortcutText("FocusAddressBar") });
            ((MenuFlyoutItem)file.Items[^1]).Click += (s, e) => DispatcherQueue.TryEnqueue(() => _ = FocusAddressBar());
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Save Window...", KeyboardAcceleratorTextOverride = "Ctrl+Shift+S" }));
            file.Items.Add(new MenuFlyoutSeparator());
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Close Window", KeyboardAcceleratorTextOverride = "Ctrl+Shift+W" }));
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Close All Windows", KeyboardAcceleratorTextOverride = "Ctrl+Alt+Shift+W" }));
            file.Items.Add(new MenuFlyoutItem { Text = "Close Tab", KeyboardAcceleratorTextOverride = ShortcutText("CloseTab") });
            ((MenuFlyoutItem)file.Items[^1]).Click += (s, e) => { BrowserTab? tab = ViewModel.ActiveTab; if (tab != null) { CloseTab(tab); } };
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Close Tabs to the Left" }));
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Close Tree" }));
            file.Items.Add(new MenuFlyoutSeparator());
            // TODO: add share options
            file.Items.Add(new MenuFlyoutSubItem { Text = "Share" });
            file.Items.Add(new MenuFlyoutSeparator());
            file.Items.Add(new MenuFlyoutItem { Text = "Save Page...", KeyboardAcceleratorTextOverride = ShortcutText("SavePage") });
            ((MenuFlyoutItem)file.Items[^1]).Click += (s, e) => _ = ShowSavePageDialog();
            file.Items.Add(new MenuFlyoutItem { Text = "Export as PDF..." });
            ((MenuFlyoutItem)file.Items[^1]).Click += (s, e) => _ = ShowExportAsPdfDialog();
            file.Items.Add(new MenuFlyoutSeparator());
            var importSub = new MenuFlyoutSubItem { Text = "Import From" };
            importSub.Items.Add(Disabled(new MenuFlyoutItem { Text = "Bookmarks HTML File..." }));
            importSub.Items.Add(new MenuFlyoutItem { Text = "Passwords CSV File..." });
            ((MenuFlyoutItem)importSub.Items[^1]).Click += (s, e) => _ = ShowImportPasswordsCsvDialog();
            file.Items.Add(importSub);
            var exportSub = new MenuFlyoutSubItem { Text = "Export" };
            exportSub.Items.Add(new MenuFlyoutItem { Text = "Bookmarks..." });
            ((MenuFlyoutItem)exportSub.Items[^1]).Click += (s, e) => _ = ShowExportBookmarksDialog();
            exportSub.Items.Add(new MenuFlyoutItem { Text = "Passwords..." });
            ((MenuFlyoutItem)exportSub.Items[^1]).Click += (s, e) => _ = ShowExportPasswordsDialog();
            file.Items.Add(exportSub);
            file.Items.Add(new MenuFlyoutSeparator());
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Page Setup...", KeyboardAcceleratorTextOverride = "Ctrl+Shift+P" }));
            file.Items.Add(Disabled(new MenuFlyoutItem { Text = "Print...", KeyboardAcceleratorTextOverride = "Ctrl+P" }));
            menu.Items.Add(file);

            // --- Edit ---
            // NOTE: not sure if we should include this menu at all; it probably must exist on macOS for reasons and I suppose
            // OrionGtk just copied it, most of the options definitely don't feel windows-native
            var edit = new MenuFlyoutSubItem { Text = "Edit" };
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Undo", KeyboardAcceleratorTextOverride = "Ctrl+Z" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Redo", KeyboardAcceleratorTextOverride = "Ctrl+Y" }));
            edit.Items.Add(new MenuFlyoutSeparator());
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Cut", KeyboardAcceleratorTextOverride = "Ctrl+X" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Copy", KeyboardAcceleratorTextOverride = "Ctrl+C" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Copy Page URL", KeyboardAcceleratorTextOverride = "Ctrl+Shift+C" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Copy URL as Markdown", KeyboardAcceleratorTextOverride = "Ctrl+Alt+Shift+C" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Paste", KeyboardAcceleratorTextOverride = "Ctrl+V" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Paste and Match Style", KeyboardAcceleratorTextOverride = "Ctrl+Alt+Shift+V" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Delete" }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Select All", KeyboardAcceleratorTextOverride = "Ctrl+A" }));
            edit.Items.Add(new MenuFlyoutSeparator());
            var find = new MenuFlyoutSubItem { Text = "Find" };
            find.Items.Add(new MenuFlyoutItem { Text = "Find", KeyboardAcceleratorTextOverride = ShortcutText("Find") });
            ((MenuFlyoutItem)find.Items[^1]).Click += (s, e) => OpenFindBar();
            find.Items.Add(new MenuFlyoutItem { Text = "Find Next", KeyboardAcceleratorTextOverride = ShortcutText("FindNext") });
            ((MenuFlyoutItem)find.Items[^1]).Click += (s, e) => FindNext(forward: true);
            find.Items.Add(new MenuFlyoutItem { Text = "Find Previous", KeyboardAcceleratorTextOverride = ShortcutText("FindPrevious") });
            ((MenuFlyoutItem)find.Items[^1]).Click += (s, e) => FindNext(forward: false);
            find.Items.Add(Disabled(new MenuFlyoutItem { Text = "Use Selection for Find", KeyboardAcceleratorTextOverride = "Ctrl+E" }));
            find.Items.Add(Disabled(new MenuFlyoutItem { Text = "Jump to Selection", KeyboardAcceleratorTextOverride = "Ctrl+J" }));
            edit.Items.Add(find);
            var spelling = new MenuFlyoutSubItem { Text = "Spelling and Grammar" };
            spelling.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Spelling and Grammar", KeyboardAcceleratorTextOverride = "Ctrl+:" }));
            spelling.Items.Add(Disabled(new MenuFlyoutItem { Text = "Check Document Now", KeyboardAcceleratorTextOverride = "Ctrl+;" }));
            spelling.Items.Add(Disabled(new MenuFlyoutSeparator()));
            spelling.Items.Add(Disabled(new MenuFlyoutItem { Text = "Check Spelling While Typing" }));
            spelling.Items.Add(Disabled(new MenuFlyoutItem { Text = "Check Grammar With Spelling" }));
            spelling.Items.Add(Disabled(new MenuFlyoutItem { Text = "Correct Spelling Automatically" }));
            edit.Items.Add(spelling);
            var subst = new MenuFlyoutSubItem { Text = "Substitutions" };
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Substitutions" }));
            subst.Items.Add(new MenuFlyoutSeparator());
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Smart Copy/Paste" }));
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Smart Quotes" }));
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Smart Dashes" }));
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Smart Links" }));
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Data Detectors" }));
            subst.Items.Add(Disabled(new MenuFlyoutItem { Text = "Text Replacement" }));
            edit.Items.Add(subst);
            var xfrm = new MenuFlyoutSubItem { Text = "Transformations" };
            xfrm.Items.Add(Disabled(new MenuFlyoutItem { Text = "Make Upper Case" }));
            xfrm.Items.Add(Disabled(new MenuFlyoutItem { Text = "Make Lower Case" }));
            xfrm.Items.Add(Disabled(new MenuFlyoutItem { Text = "Capitalize" }));
            edit.Items.Add(xfrm);
            var speech = new MenuFlyoutSubItem { Text = "Speech" };
            speech.Items.Add(Disabled(new MenuFlyoutItem { Text = "Start Speaking" }));
            speech.Items.Add(Disabled(new MenuFlyoutItem { Text = "Stop Speaking" }));
            edit.Items.Add(speech);
            edit.Items.Add(new MenuFlyoutSeparator());
            var autofill = new MenuFlyoutSubItem { Text = "AutoFill" };
            autofill.Items.Add(Disabled(new MenuFlyoutItem { Text = "Contact..." }));
            autofill.Items.Add(Disabled(new MenuFlyoutItem { Text = "Passwords..." }));
            edit.Items.Add(autofill);
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Start Dictation..." }));
            edit.Items.Add(Disabled(new MenuFlyoutItem { Text = "Emoji & Symbols" }));
            menu.Items.Add(edit);


            // --- View ---
            var view = new MenuFlyoutSubItem { Text = "View" };
            var alwaysShowToolbar = new ToggleMenuFlyoutItem { Text = "Always Show Toolbar in Full Screen" };
            alwaysShowToolbar.Click += (s, e) =>
            {
                // Live-apply happens via the setting observer in MainWindowContent.Fullscreen.cs.
                OrionBackend.SetBoolSetting(ViewModel.ActiveProfileId,
                    "settings::appearance::always_show_toolbar_in_fullscreen", "settings::appearance",
                    "always_show_toolbar_in_fullscreen", alwaysShowToolbar.IsChecked, true);
            };
            menu.Opening += (s, e) => alwaysShowToolbar.IsChecked = GetAlwaysShowToolbarInFullscreen();
            view.Items.Add(alwaysShowToolbar);
            var customizeToolbar = new MenuFlyoutItem { Text = "Customize Toolbar..." };
            customizeToolbar.Click += async (s, e) => await ShowCustomizeToolbarDialogAsync();
            view.Items.Add(customizeToolbar);
            view.Items.Add(new MenuFlyoutSeparator());
            view.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Always Show Tab Bar" }));
            var showBookmarkBar = new ToggleMenuFlyoutItem { Text = "Show Bookmarks Bar", KeyboardAcceleratorTextOverride = "Ctrl+Shift+B" };
            showBookmarkBar.Click += (s, e) => ViewModel.ShowBookmarkBar = showBookmarkBar.IsChecked;
            menu.Opening += (s, e) => showBookmarkBar.IsChecked = ViewModel.ShowBookmarkBar;
            view.Items.Add(showBookmarkBar);
            view.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Show Status Bar" }));
            view.Items.Add(new MenuFlyoutSeparator());
            // TODO: the shortcuts with Super in them are straight from OrionGtk, which I assume copied them from macOS.
            // we should come up with more native shortcuts (not sure if we can even use shortcuts with the Win key in them at all;
            // the OS might capture some)
            var showSidebar = new ToggleMenuFlyoutItem { Text = "Show Sidebar", KeyboardAcceleratorTextOverride = ShortcutText("ShowSidebar") };
            showSidebar.Click += (s, e) => ViewModel.ShowSidebar = !ViewModel.ShowSidebar;
            menu.Opening += (s, e) => showSidebar.IsChecked = ViewModel.ShowSidebar;
            view.Items.Add(showSidebar);
            SidebarPage[] sidebarPages = Enum.GetValues<SidebarPage>()[1..];
            var sidebarRadios = new ToggleMenuFlyoutItem[sidebarPages.Length];
            for (int i = 0; i < sidebarPages.Length; i++)
            {
                SidebarPage page = sidebarPages[i];
                var radio = new ToggleMenuFlyoutItem
                {
                    Text = $"Show {page.DisplayName()} Sidebar",
                    KeyboardAcceleratorTextOverride = ShortcutText($"Sidebar{page}"),
                };
                radio.Click += (s, e) => Shortcuts[$"Sidebar{page}"].Action!();
                sidebarRadios[i] = radio;
                view.Items.Add(radio);
            }
            menu.Opening += (s, e) =>
            {
                for (int i = 0; i < sidebarPages.Length; i++)
                    sidebarRadios[i].IsChecked = ViewModel.ShowSidebar && sidebarPages[i] == ViewModel.SelectedSidebarPage;
            };
            view.Items.Add(new MenuFlyoutSeparator());
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Reader", KeyboardAcceleratorTextOverride = "Shift+Super+R" }));
            view.Items.Add(new MenuFlyoutItem { Text = "Show Tab Overview", KeyboardAcceleratorTextOverride = ShortcutText("TabOverview") });
            ((MenuFlyoutItem)view.Items[^1]).Click += TabOverviewButton_Click;
            view.Items.Add(new MenuFlyoutItem { Text = "Show Downloads", KeyboardAcceleratorTextOverride = ShortcutText("ShowDownloads") });
            ((MenuFlyoutItem)view.Items[^1]).Click += DownloadsButton_Click;
            view.Items.Add(new MenuFlyoutSeparator());
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Enable Focus Mode", KeyboardAcceleratorTextOverride = "Shift+Super+F" }));
            view.Items.Add(new MenuFlyoutSeparator());
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Stop", KeyboardAcceleratorTextOverride = "Esc" })); // GTK shortcut: Super+.
            view.Items.Add(new MenuFlyoutItem { Text = "Reload This Page", KeyboardAcceleratorTextOverride = ShortcutText("Reload") });
            ((MenuFlyoutItem)view.Items[^1]).Click += (s, e) => Shortcuts["Reload"].Action!();
            view.Items.Add(new MenuFlyoutItem { Text = "Reload Page Without Cache", KeyboardAcceleratorTextOverride = ShortcutText("ReloadBypassingCache") });
            ((MenuFlyoutItem)view.Items[^1]).Click += (s, e) => Shortcuts["ReloadBypassingCache"].Action!();
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Reload All Tabs in Window", KeyboardAcceleratorTextOverride = "Ctrl+Alt+Shift+R" })); // GTK shortcut: Ctrl+Alt+Super+R
            view.Items.Add(new MenuFlyoutSeparator());
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Picture in Picture Video", KeyboardAcceleratorTextOverride = "Shift+Super+P" }));
            view.Items.Add(new MenuFlyoutSeparator());
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Actual Size", KeyboardAcceleratorTextOverride = "Ctrl+0" }));
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Zoom In", KeyboardAcceleratorTextOverride = "Ctrl++" }));
            view.Items.Add(Disabled(new MenuFlyoutItem { Text = "Zoom Out", KeyboardAcceleratorTextOverride = "Ctrl+-" }));
            view.Items.Add(new MenuFlyoutSeparator());
            var fullScreenItem = new MenuFlyoutItem { Text = "Enter Full Screen", KeyboardAcceleratorTextOverride = ShortcutText("ToggleFullscreen") };
            fullScreenItem.Click += (s, e) => ToggleFullscreen();
            menu.Opening += (s, e) => fullScreenItem.Text = _isFullscreen ? "Exit Full Screen" : "Enter Full Screen";
            view.Items.Add(fullScreenItem);
            menu.Items.Add(view);

            // --- History ---
            var history = new MenuFlyoutSubItem { Text = "History" };
            history.Items.Add(new MenuFlyoutItem { Text = "Show History", KeyboardAcceleratorTextOverride = ShortcutText("ShowHistory") });
            ((MenuFlyoutItem)history.Items[^1]).Click += History_Click;
            var clearHistory = new MenuFlyoutItem { Text = "Clear History..." };
            clearHistory.Click += async (s, e) =>
            {
                bool confirmed = await PopupDialog.ShowConfirmAsync(
                    this,
                    "Clear History",
                    "Are you sure you want to clear all browsing history? This cannot be undone.",
                    confirmButton: "Clear");
                if (confirmed)
                    OrionBackend.ClearHistory(ViewModel.ActiveProfileId);
            };
            history.Items.Add(clearHistory);
            history.Items.Add(new MenuFlyoutSeparator());
            var backItem = new MenuFlyoutItem { Text = "Back" };
            backItem.Click += (s, e) => ViewModel.ActiveTabViewModel?.GoBackCommand.Execute(null);
            history.Items.Add(backItem);
            var forwardItem = new MenuFlyoutItem { Text = "Forward" };
            forwardItem.Click += (s, e) => ViewModel.ActiveTabViewModel?.GoForwardCommand.Execute(null);
            history.Items.Add(forwardItem);
            var homeItem = new MenuFlyoutItem { Text = "Home", KeyboardAcceleratorTextOverride = "Ctrl+Shift+H" };
            homeItem.Click += (s, e) => NavigateToUrl("about:blank");
            history.Items.Add(homeItem);
            history.Items.Add(new MenuFlyoutSeparator());
            var recentlyClosed = new MenuFlyoutSubItem { Text = "Recently Closed" };
            history.Items.Add(recentlyClosed);
            var reopenLast = new MenuFlyoutItem
            {
                Text = "Reopen Last Closed Tab / Window",
                KeyboardAcceleratorTextOverride = ShortcutText("UndoCloseTab")
            };
            reopenLast.Click += (s, e) => ViewModel.UndoCloseTab();
            history.Items.Add(reopenLast);
            history.Items.Add(Disabled(new MenuFlyoutItem { Text = "Reopen All Windows from Last Session" }));
            history.Items.Add(new MenuFlyoutSeparator());
            int historyInsertIndex = history.Items.Count;
            menu.Opening += (s, e) =>
            {
                while (history.Items.Count > historyInsertIndex)
                    history.Items.RemoveAt(history.Items.Count - 1);

                PopulateRecentlyClosed(recentlyClosed);
                reopenLast.IsEnabled = recentlyClosed.Items.Count > 0;

                DateTime today = DateTime.Today;
                List<HistoryEntry> entries = OrionBackend.GetHistoryForMenu(ViewModel.ActiveProfileId, 4);

                // Today's items (up to 6, matching GTK)
                int todayCount = 0;
                foreach (HistoryEntry entry in entries.Where(h => h.VisitedAt.Date == today))
                {
                    var item = new MenuFlyoutItem { Text = entry.Website };
                    string url = entry.Address;
                    item.Click += (s2, e2) => NavigateToUrl(url);
                    history.Items.Add(item);
                    _ = LoadMenuFlyoutFaviconAsync(url, item);
                    if (++todayCount >= 6) break;
                }
                if (history.Items.Count > historyInsertIndex)
                    history.Items.Add(new MenuFlyoutSeparator());

                // Older days as subflyouts
                foreach (IGrouping<DateTime, HistoryEntry> group in entries
                    .Where(h => h.VisitedAt.Date < today)
                    .GroupBy(h => h.VisitedAt.Date)
                    .OrderByDescending(g => g.Key))
                {
                    var daySub = new MenuFlyoutSubItem { Text = group.Key.ToString("dddd dd MMMM yyyy") };
                    foreach (HistoryEntry entry in group)
                    {
                        var item = new MenuFlyoutItem { Text = entry.Website };
                        string url = entry.Address;
                        item.Click += (s2, e2) => NavigateToUrl(url);
                        daySub.Items.Add(item);
                        _ = LoadMenuFlyoutFaviconAsync(url, item);
                    }
                    history.Items.Add(daySub);
                    history.Items.Add(new MenuFlyoutSeparator());
                }
            };
            menu.Items.Add(history);

            // --- Bookmarks ---
            var bookmarks = new MenuFlyoutSubItem { Text = "Bookmarks" };
            var showBookmarks = new MenuFlyoutItem { Text = "Show Bookmarks...", KeyboardAcceleratorTextOverride = "Ctrl+Shift+B" };
            showBookmarks.Click += (s, e) => LibraryWindow.ShowHistory("Bookmarks", ViewModel.ActiveProfileId);
            bookmarks.Items.Add(showBookmarks);
            var showReadingList = new MenuFlyoutItem { Text = "Show Reading List...", KeyboardAcceleratorTextOverride = "Alt+Shift+B" };
            showReadingList.Click += (s, e) => LibraryWindow.ShowHistory("ReadingList", ViewModel.ActiveProfileId);
            bookmarks.Items.Add(showReadingList);
            bookmarks.Items.Add(new MenuFlyoutSeparator());
            bookmarks.Items.Add(new MenuFlyoutItem { Text = "Add Bookmark...", KeyboardAcceleratorTextOverride = ShortcutText("AddBookmark") });
            ((MenuFlyoutItem)bookmarks.Items[^1]).Click += BookmarkButton_Click;
            bookmarks.Items.Add(Disabled(new MenuFlyoutItem { Text = "Add Bookmarks for Current Tabs..." }));
            var addBookmarkFolder = new MenuFlyoutItem { Text = "Add Bookmark Folder" };
            addBookmarkFolder.Click += (s, e) => _ = ShowAddBookmarkFolderDialog();
            bookmarks.Items.Add(addBookmarkFolder);
            bookmarks.Items.Add(new MenuFlyoutSeparator());
            var addToReadingList = new MenuFlyoutItem { Text = "Add to Reading List" };
            addToReadingList.Click += (s, e) =>
            {
                BrowserTab? tab = ViewModel.ActiveTab;
                if (tab != null && !string.IsNullOrWhiteSpace(tab.Url))
                {
                    _ = OrionBackend.AddReadingListEntry(ViewModel.ActiveProfileId, tab.Title ?? tab.Url, tab.Url);
                    ReadingListChanged.Notify();
                }
            };
            bookmarks.Items.Add(addToReadingList);
            bookmarks.Items.Add(new MenuFlyoutSeparator());
            var recentlyBookmarked = new MenuFlyoutSubItem { Text = "Recently Bookmarked" };
            menu.Opening += (s, e) =>
            {
                recentlyBookmarked.Items.Clear();
                foreach (BookmarkEntry entry in OrionBackend.GetRecentBookmarks(ViewModel.ActiveProfileId))
                {
                    var item = new MenuFlyoutItem { Text = entry.Title };
                    string url = entry.Url;
                    string title = entry.Title;
                    item.Click += (s2, e2) => OpenBookmark(url, title);
                    recentlyBookmarked.Items.Add(item);
                    _ = LoadMenuFlyoutFaviconAsync(url, item);
                }
                if (recentlyBookmarked.Items.Count == 0)
                    recentlyBookmarked.Items.Add(Disabled(new MenuFlyoutItem { Text = "(No recent bookmarks)" }));
            };
            bookmarks.Items.Add(recentlyBookmarked);
            bookmarks.Items.Add(new MenuFlyoutSeparator());
            // Bookmark folder submenus — populated dynamically on menu open
            int bookmarkFoldersInsertIndex = bookmarks.Items.Count;
            menu.Opening += (s, e) =>
            {
                // Remove previously added folder items (everything from insert index to end)
                while (bookmarks.Items.Count > bookmarkFoldersInsertIndex)
                    bookmarks.Items.RemoveAt(bookmarks.Items.Count - 1);

                InitializeBookmarks();
                foreach (BookmarkTreeNode node in _bookmarkNodes)
                {
                    if (node.IsFolder)
                    {
                        var folderSub = new MenuFlyoutSubItem { Text = node.Title };
                        PopulateBookmarkFolderMenu(folderSub, node);
                        bookmarks.Items.Add(folderSub);
                    }
                    else
                    {
                        var item = new MenuFlyoutItem { Text = node.Title };
                        string url = node.Url;
                        string title = node.Title;
                        item.Click += (s2, e2) => OpenBookmark(url, title);
                        bookmarks.Items.Add(item);
                        _ = LoadMenuFlyoutFaviconAsync(node, item);
                    }
                }
            };
            menu.Items.Add(bookmarks);

            // --- Tools ---
            var tools = new MenuFlyoutSubItem { Text = "Tools" };
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Passwords", KeyboardAcceleratorTextOverride = "Ctrl+Shift+O" }));
            var extensions = new MenuFlyoutSubItem { Text = "Extensions" };
            extensions.Items.Add(new MenuFlyoutItem { Text = "Manage Extensions..." });
            ((MenuFlyoutItem)extensions.Items[^1]).Click += (_, _) => OpenSettingsTab("Extensions");
            // Dynamic extension list inserted here on menu open
            int extDynamicStart = extensions.Items.Count;
            extensions.Items.Add(new MenuFlyoutSeparator());
            extensions.Items.Add(Disabled(new MenuFlyoutItem { Text = "Install from Firefox Store" }));
            extensions.Items.Add(Disabled(new MenuFlyoutItem { Text = "Install from Chrome Store" }));
            extensions.Items.Add(Disabled(new MenuFlyoutItem { Text = "Install from Disk..." }));
            extensions.Items.Add(new MenuFlyoutSeparator());
            extensions.Items.Add(new MenuFlyoutItem { Text = "Popular Extensions" });
            ((MenuFlyoutItem)extensions.Items[^1]).Click += PopularExtensions_Click;
            int extDynamicCount = 0;
            menu.Opening += (s, e) =>
            {
                for (int i = 0; i < extDynamicCount; i++)
                    extensions.Items.RemoveAt(extDynamicStart);
                extDynamicCount = 0;

                List<ExtensionEntry> installed = OrionBackend.GetInstalledExtensions(ViewModel.ActiveProfileId);
                foreach (ExtensionEntry ext in installed)
                {
                    extensions.Items.Insert(extDynamicStart + extDynamicCount, BuildExtensionSubItem(ext));
                    extDynamicCount++;
                }
            };
            tools.Items.Add(extensions);
            // orion-linux binds Notes to Ctrl+Super+I; the Windows key isn't a
            // usable modifier here and Ctrl+Shift+I is the inspector's.
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Notes" }));
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(new MenuFlyoutItem { Text = "Low Power Mode..." });
            ((MenuFlyoutItem)tools.Items[^1]).Click += async (s, e) =>
            {
                bool confirmed = await PopupDialog.ShowConfirmAsync(
                    this,
                    "Turn on Low Power Mode?",
                    "Reduces power usage by instantly hibernating all tabs after 5 minutes, except for the current tab and pinned tabs.",
                    confirmButton: "Enable");

                // TODO: implement low power mode when backend supports it
            };
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Enable Compatibility Mode", KeyboardAcceleratorTextOverride = "Ctrl+Shift+E" }));
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Install This Site as an App" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Manage Apps..." }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Take Screenshot" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Take Screenshot of Entire Page" }));
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Edit Text on Page" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Allow Copy & Paste" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Create QR Code for This Page" }));
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Fonts...", KeyboardAcceleratorTextOverride = "Ctrl+Alt+Shift+F" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Color Picker..." }));
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Page Tweaks..." }));
            tools.Items.Add(new MenuFlyoutSeparator());
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Page in Internet Archive" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Summarize Page" }));
            tools.Items.Add(Disabled(new MenuFlyoutItem { Text = "Translate with Kagi" }));
            menu.Items.Add(tools);

            // --- Develop ---
            var develop = new MenuFlyoutSubItem { Text = "Develop" };
            develop.Items.Add(new MenuFlyoutItem { Text = "Show Web Inspector", KeyboardAcceleratorTextOverride = ShortcutText("ToggleDevTools") });
            ((MenuFlyoutItem)develop.Items[^1]).Click += (_, _) => ToggleDevTools();
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Inspect Element" }));
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Design Tools", KeyboardAcceleratorTextOverride = "Ctrl+Alt+Shift+D" }));
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Console Errors Indicator" }));
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Enter Responsive Design Mode" })); // OrionGtk: Ctrl+Super+R
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Console" })); // OrionGtk: Alt+Super+C
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Network" })); // OrionGtk: Alt+Super+N
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Storage" })); // OrionGtk: Alt+Super+O
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Resources" })); // OrionGtk: Alt+Super+A
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Layers" }));
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Open Graphics" })); // OrionGtk: Alt+Super+G
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Start Performance Profile" })); // OrionGtk: Alt+Super+P
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Empty Cookies and Storage for This Site" }));
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Edit Cookies" })); // OrionGtk: Ctrl+Super+C
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Empty Cache" })); // OrionGtk: Alt+Super+E
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable JavaScript" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable Cookies" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable Content Blockers" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable Tracking Prevention" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable Images" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable Web Fonts" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Disable Cross-Origin Restrictions" }));
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Allow JavaScript from Apple Events" }));
            develop.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Allow JavaScript from Address Bar" }));
            develop.Items.Add(new MenuFlyoutSeparator());
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "View Source", KeyboardAcceleratorTextOverride = "Ctrl+U" }));
            develop.Items.Add(Disabled(new MenuFlyoutItem { Text = "Validate HTML", KeyboardAcceleratorTextOverride = "Ctrl+U" }));
            develop.Items.Add(new MenuFlyoutSeparator());
            var userAgent = new MenuFlyoutSubItem { Text = "User Agent" };
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Automatic", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Custom", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Chrome", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Firefox", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Safari", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Safari iPhone", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Safari iPad", GroupName = "UserAgent" }));
            userAgent.Items.Add(Disabled(new RadioMenuFlyoutItem { Text = "Edge", GroupName = "UserAgent" }));
            develop.Items.Add(userAgent);
            // NOTE: OrionGtk has "Experimental Features" menu here which has a *lot* of stuff in it.
            // I've left it out until we actually need to implement one of those experimental features.
            menu.Items.Add(develop);

            // --- Window ---
            var window = new MenuFlyoutSubItem { Text = "Window" };
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Minimize" })); // OrionGtk: Super+M
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Zoom" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Fill" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Center" }));
            // NOTE: OrionGtk has "Move & Resize" and "Full Screen Tile" submenus here; they deal with window management,
            // so I've left them out for now.
            window.Items.Add(new MenuFlyoutSeparator());
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Previous Tab", KeyboardAcceleratorTextOverride = "Ctrl+Shift+Tab" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Show Next Tab", KeyboardAcceleratorTextOverride = "Ctrl+Tab" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Go to Previous Tab Group", KeyboardAcceleratorTextOverride = "Ctrl+Shift+Up" })); // OrionGtk: Shift+Super+Up
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Go to Next Tab Group", KeyboardAcceleratorTextOverride = "Ctrl+Shift+Down" })); // OrionGtk: Shift+Super+Down
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Pin Tab" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Duplicate Tab" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Suspend Tab" })); // OrionGtk: Shift+Ctrl+Super+S
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Move Tab to New Window" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Merge All Windows" }));
            window.Items.Add(new MenuFlyoutSeparator());
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Mute Tab" }));
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Mute Other Tabs" }));
            window.Items.Add(new MenuFlyoutSeparator());
            window.Items.Add(Disabled(new MenuFlyoutItem { Text = "Bring All to Front" }));
            window.Items.Add(new MenuFlyoutSeparator());
            window.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Float on Top" }));
            window.Items.Add(new MenuFlyoutSeparator());
            // TODO: list of windows here
            window.Items.Add(Disabled(new ToggleMenuFlyoutItem { Text = "Window 1 - Placeholder item" }));
            menu.Items.Add(window);

            // --- Help ---
            var help = new MenuFlyoutSubItem { Text = "Help" };
            help.Items.Add(Disabled(new MenuFlyoutItem { Text = "Orion Help" }));
            help.Items.Add(Disabled(new MenuFlyoutItem { Text = "Troubleshoot Website Issues" }));
            help.Items.Add(new MenuFlyoutSeparator());
            help.Items.Add(Disabled(new MenuFlyoutItem { Text = "Orion Feedback" }));
            help.Items.Add(Disabled(new MenuFlyoutItem { Text = "Orion Discord" }));
            help.Items.Add(Disabled(new MenuFlyoutItem { Text = "Copy Debug Info to Clipboard" }));
            help.Items.Add(new MenuFlyoutSeparator());
            help.Items.Add(Disabled(new MenuFlyoutItem { Text = "Release Notes" }));
            menu.Items.Add(help);

            menu.Items.Add(new MenuFlyoutSeparator());
            var exit = new MenuFlyoutItem { Text = "Exit", KeyboardAcceleratorTextOverride = ShortcutText("Exit") };
            exit.Click += (s, e) => RequestExitAll(quitAllProfiles: true);
            menu.Items.Add(exit);

            MenuButton.Flyout = menu;
        }

        private void PopulateRecentlyClosed(MenuFlyoutSubItem menu)
        {
            menu.Items.Clear();

            List<OrionBackend.RecentlyClosedTab> tabs = OrionBackend.GetRecentlyClosedTabs(ViewModel.ActiveProfileId);
            List<OrionBackend.RecentlyClosedWindow> windows = OrionBackend.GetRecentlyClosedWindows(ViewModel.ActiveProfileId);

            // Build a map of window_id -> tabs for grouping
            var windowTabsMap = new Dictionary<long, List<OrionBackend.RecentlyClosedTab>>();
            var standaloneTabs = new List<OrionBackend.RecentlyClosedTab>();
            foreach (OrionBackend.RecentlyClosedTab tab in tabs)
            {
                if (tab.WindowId != 0)
                {
                    if (!windowTabsMap.TryGetValue(tab.WindowId, out List<OrionBackend.RecentlyClosedTab>? list))
                    {
                        list = [];
                        windowTabsMap[tab.WindowId] = list;
                    }
                    list.Add(tab);
                }
                else
                {
                    standaloneTabs.Add(tab);
                }
            }

            // Merge standalone tabs and windows into a single list sorted by closed_at descending
            var items = new List<(DateTime ClosedAt, OrionBackend.RecentlyClosedTab? Tab, OrionBackend.RecentlyClosedWindow? Window)>();
            foreach (OrionBackend.RecentlyClosedTab tab in standaloneTabs)
                items.Add((tab.ClosedAt, tab, null));
            foreach (OrionBackend.RecentlyClosedWindow window in windows)
                items.Add((window.ClosedAt, null, window));
            items.Sort((a, b) => b.ClosedAt.CompareTo(a.ClosedAt));

            if (items.Count == 0)
            {
                menu.Items.Add(Disabled(new MenuFlyoutItem { Text = "(No Recently Closed Items)" }));
                return;
            }

            foreach ((_, OrionBackend.RecentlyClosedTab? tab, OrionBackend.RecentlyClosedWindow? window) in items)
            {
                if (tab != null)
                {
                    // Standalone closed tab
                    var item = new MenuFlyoutItem { Text = tab.Name.Length > 0 ? tab.Name : tab.Uri };
                    long tabId = tab.Id;
                    string uri = tab.Uri;
                    item.Click += (s, e) =>
                    {
                        OrionBackend.DeleteRecentlyClosedTabs([tabId]);
                        _ = ViewModel.CreateNewTab(uri);
                    };
                    menu.Items.Add(item);
                }
                else if (window != null)
                {
                    List<OrionBackend.RecentlyClosedTab>? windowTabs =
                        windowTabsMap.GetValueOrDefault(window.Id);

                    if (windowTabs is { Count: 1 })
                    {
                        // Single-tab window — show as flat item
                        OrionBackend.RecentlyClosedTab singleTab = windowTabs[0];
                        var item = new MenuFlyoutItem { Text = singleTab.Name.Length > 0 ? singleTab.Name : singleTab.Uri };
                        long wId = window.Id;
                        string uri = singleTab.Uri;
                        item.Click += (s, e) =>
                        {
                            OrionBackend.DeleteRecentlyClosedWindows([wId]);
                            _ = ViewModel.CreateNewTab(uri);
                        };
                        menu.Items.Add(item);
                    }
                    else if (windowTabs is { Count: > 1 })
                    {
                        // Multi-tab window — show as submenu
                        string label = $"{windowTabs[0].Name} (and {windowTabs.Count - 1} other tab{(windowTabs.Count - 1 == 1 ? "" : "s")})";
                        var sub = new MenuFlyoutSubItem { Text = label };

                        long wId = window.Id;
                        var reopenAll = new MenuFlyoutItem { Text = "Reopen Window" };
                        reopenAll.Click += (s, e) => ReopenClosedWindow(wId, windowTabs);
                        sub.Items.Add(reopenAll);
                        sub.Items.Add(new MenuFlyoutSeparator());

                        foreach (OrionBackend.RecentlyClosedTab wTab in windowTabs)
                        {
                            var item = new MenuFlyoutItem { Text = wTab.Name.Length > 0 ? wTab.Name : wTab.Uri };
                            long tabId = wTab.Id;
                            string uri = wTab.Uri;
                            item.Click += (s, e) =>
                            {
                                OrionBackend.DeleteRecentlyClosedTabs([tabId]);
                                _ = ViewModel.CreateNewTab(uri);
                            };
                            sub.Items.Add(item);
                        }
                        menu.Items.Add(sub);
                    }
                }
            }
        }

        private void ReopenClosedWindow(long windowId, List<OrionBackend.RecentlyClosedTab> tabs)
        {
            OrionBackend.DeleteRecentlyClosedWindows([windowId]);
            var newWindow = new MainWindow(ViewModel.ActiveProfile);
            newWindow.Activate();
            MainWindowViewModel vm = newWindow.MainWindowContent.ViewModel;
            // Remove the default blank tab created by the constructor
            BrowserTab? blankTab = vm.ActiveTab;
            foreach (OrionBackend.RecentlyClosedTab tab in tabs)
                _ = vm.CreateNewTab(tab.Uri, tab.Name);
            if (blankTab != null)
                _ = vm.CloseTab(blankTab);
        }

        private void PopulateBookmarkFolderMenu(MenuFlyoutSubItem parent, BookmarkTreeNode folder)
        {
            if (folder.Children.Count == 0)
            {
                parent.Items.Add(Disabled(new MenuFlyoutItem { Text = "(Empty)" }));
                return;
            }
            foreach (BookmarkTreeNode child in folder.Children)
            {
                if (child.IsFolder)
                {
                    var sub = new MenuFlyoutSubItem { Text = child.Title };
                    PopulateBookmarkFolderMenu(sub, child);
                    parent.Items.Add(sub);
                }
                else
                {
                    var item = new MenuFlyoutItem { Text = child.Title };
                    string url = child.Url;
                    string title = child.Title;
                    item.Click += (s, e) => OpenBookmark(url, title);
                    parent.Items.Add(item);
                    _ = LoadMenuFlyoutFaviconAsync(child, item);
                }
            }
        }

        private void PopularExtensions_Click(object sender, RoutedEventArgs e) => PopularExtensionsWindow.Show(profileId: ViewModel.ActiveProfileId);

        private MenuFlyoutSubItem BuildExtensionSubItem(ExtensionEntry ext)
        {
            string label = ext.IsEnabled ? ext.Name : $"{ext.Name} (disabled)";
            var sub = new MenuFlyoutSubItem { Text = label };
            string toggleLabel = ext.IsEnabled ? "Disable" : "Enable";
            var toggleItem = new MenuFlyoutItem { Text = toggleLabel };
            string extId = ext.Id;
            bool newState = !ext.IsEnabled;
            toggleItem.Click += (_, _) => OrionBackend.SetExtensionEnabled(extId, newState);
            sub.Items.Add(toggleItem);
            return sub;
        }

        private async Task ShowCustomizeToolbarDialogAsync()
        {
            var tcs = new TaskCompletionSource();
            var content = new CustomizeToolbarControl();
            content.DoneRequested += (s, e) => tcs.TrySetResult();

            var overlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                Width = ActualWidth,
                Height = ActualHeight
            };
            overlay.Children.Add(content);

            var popup = new Popup
            {
                Child = overlay,
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = false,
                XamlRoot = XamlRoot
            };

            void OnSizeChanged(object s, SizeChangedEventArgs e)
            {
                overlay.Width = ActualWidth;
                overlay.Height = ActualHeight;
            }

            SizeChanged += OnSizeChanged;
            popup.IsOpen = true;
            await tcs.Task;
            popup.IsOpen = false;
            SizeChanged -= OnSizeChanged;
        }

        private void ProfileEmojiPicker_Click(object sender, RoutedEventArgs e) => OpenSettingsTab("Profiles");

        private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
        {
            long timestamp = OrionBackend.GetLastSnapshotTimestamp(ViewModel.ActiveProfileId);
            if (timestamp == 0)
            {
                await PopupDialog.ShowConfirmAsync(this,
                    "No Snapshot",
                    "No workspace snapshot has been created yet.",
                    confirmButton: "OK",
                    cancelButton: "");
                return;
            }

            DateTime snapshotTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
            string formatted = snapshotTime.Date == DateTime.Today
                ? $"Today, {snapshotTime:h:mm tt}"
                : snapshotTime.Date == DateTime.Today.AddDays(-1)
                    ? $"Yesterday, {snapshotTime:h:mm tt}"
                    : snapshotTime.ToString("g");

            bool confirmed = await PopupDialog.ShowConfirmAsync(this,
                "Restore Snapshot",
                $"Are you sure you want to restore the snapshot taken on {formatted}?\n\n"
                + "Your current session will be saved as a new snapshot before restoring.",
                confirmButton: "Restore",
                cancelButton: "Cancel");
            if (!confirmed) return;

            OrionBackend.RestoreSnapshot(ViewModel.ActiveProfileId, timestamp);
            WebViewHost.HideAll();
            WebViewHost.Clear();
            ViewModel.RestoreFromSnapshot();
        }

        /// <summary>
        /// Helper to mark a menu item as disabled (not yet implemented).
        /// </summary>
        private static T Disabled<T>(T item) where T : MenuFlyoutItemBase
        {
            item.IsEnabled = false;
            return item;
        }
    }
}
