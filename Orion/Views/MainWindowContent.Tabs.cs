using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        /// <summary>
        /// Guard flag: suppresses TabView.SelectionChanged while we are
        /// programmatically modifying the tab collection (close, close others,
        /// close to the right, move to group). Without this, the
        /// CollectionChanged(Reset) fired by each removal causes TabView to
        /// auto-select a tab and fire SelectionChanged synchronously — before
        /// our code sets the intended ActiveTab — producing double activations,
        /// spurious WebView creation, and flicker.
        /// </summary>
        private bool _suppressTabStripSelection;

        /// <summary>
        /// Subscribes to ViewModel.PropertyChanged so that all ActiveTab
        /// changes (from any source) are handled in one place.
        /// </summary>
        private void InitializeTabTracking()
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateCompactTabsColumn();
            UpdateVerticalTabStripSource();

            // Subscribe to horizontal tab reorder events for each group
            foreach (TabGroupViewModel vm in ViewModel.TabGroups)
                vm.Model.FlattenedTabCollection.Reordered += OnHorizontalTabReordered;
            ViewModel.TabGroups.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (TabGroupViewModel vm in e.NewItems)
                        vm.Model.FlattenedTabCollection.Reordered += OnHorizontalTabReordered;
                }

                if (e.OldItems != null)
                {
                    foreach (TabGroupViewModel vm in e.OldItems)
                        vm.Model.FlattenedTabCollection.Reordered -= OnHorizontalTabReordered;
                }
            };
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ActiveTab))
            {
                BrowserTab? tab = ViewModel.ActiveTab;
                if (tab != null)
                    OnActiveTabChanged(tab);
                else
                    UpdateWindowTitle();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.TabLayout))
            {
                UpdateCompactTabsColumn();
                if (ViewModel.ActiveTab is { } tab)
                {
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ViewModel.TabLayout == TabLayout.standard)
                            HorizontalTabStrip.SelectedItem = tab;
                        else if (ViewModel.TabLayout == TabLayout.compact)
                            CompactTabStrip.SelectedItem = tab;
                    });
                }
            }
            else if (e.PropertyName is nameof(MainWindowViewModel.ShowNestedVerticalTabs)
                or nameof(MainWindowViewModel.ActiveGroupTabs))
            {
                UpdateVerticalTabStripSource();
            }
        }

        private DataTemplate? _nestedVerticalTabTemplate;

        private void UpdateVerticalTabStripSource()
        {
            _nestedVerticalTabTemplate ??= VerticalTabStrip.ItemTemplate;

            if (ViewModel.ShowNestedVerticalTabs)
            {
                VerticalTabStrip.ItemTemplate = _nestedVerticalTabTemplate;
                VerticalTabStrip.ItemsSource = ViewModel.ActiveGroupTabs;
            }
            else
            {
                VerticalTabStrip.ItemTemplate = (DataTemplate)Resources["FlatVerticalTabTemplate"];
                VerticalTabStrip.ItemsSource = ViewModel.FlattenedActiveGroupTabs;
            }
            TrackPinFolderGroup();
        }

        private void UpdateCompactTabsColumn()
        {
            CompactTabsColumn.Width = ViewModel.TabLayout == TabLayout.compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        }

        /// <summary>The previously active tab, captured on switch-away.</summary>
        private BrowserTab? _lastActiveTab;

        /// <summary>
        /// Reacts to any ActiveTab change: ensures web view exists, navigates, and syncs the horizontal tab strip.
        /// </summary>
        private void OnActiveTabChanged(BrowserTab tab)
        {
            // Capture the outgoing web tab's thumbnail while its view is
            // still the visible one (hidden views can't paint). Internal
            // tabs capture at activation instead — their async XAML render
            // would run only after this handler has already hidden them.
            if (_lastActiveTab != null && _lastActiveTab != tab && !_lastActiveTab.IsInternalPage)
                RequestTabThumbnail(_lastActiveTab);
            _lastActiveTab = tab;
            if (tab.IsInternalPage)
                ScheduleInternalPageThumbnail(tab);

            tab.IsHibernated = false;
            CloseWebContextMenu();
            // An open consent prompt belongs to the outgoing tab; closing it
            // denies that request once.
            PermissionPromptControl.Close();
            ExitElementFullscreenOnTabSwitch(tab);
            SyncFindBarToActiveTab(tab);

            bool alreadyLoaded = ViewModel.GetOrCreateTabViewModel(tab).NativeWebView != null;
            if (alreadyLoaded)
                ShowActiveTab(tab);
            else
                NavigateToUrl(tab.Url);
            SyncDevToolsPaneToActiveTab(tab);

            // Mirror the active tab's title into the OS window title.
            // -= then += keeps this at one handler per tab no matter how often
            // the tab is re-activated; the handler itself filters on sender.
            tab.PropertyChanged -= OnActiveTabTitleChanged;
            tab.PropertyChanged += OnActiveTabTitleChanged;
            UpdateWindowTitle();

            // Defer tab strip sync — nodes may not exist yet for newly added tabs
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (tab != ViewModel.ActiveTab)
                    return;
                if (ViewModel.TabLayout == TabLayout.standard)
                    HorizontalTabStrip.SelectedItem = tab;
                else if (ViewModel.TabLayout == TabLayout.compact)
                    CompactTabStrip.SelectedItem = tab;
                if (ViewModel.ShowSidebar && ViewModel.SelectedSidebarPage == SidebarPage.tabs)
                    VerticalTabStrip.SelectedItem = tab.IsInPinFolder ? null : tab;
                if (tab.Url == "about:blank")
                    _ = FocusAddressBar();
            });
        }

        private void OnActiveTabTitleChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BrowserTab.Title) && sender == ViewModel.ActiveTab)
                UpdateWindowTitle();
        }

        /// <summary>
        /// Writes the current active tab's title to the parent Window's title bar.
        /// Falls back to "Orion Browser" when no active tab or no title yet.
        /// </summary>
        private void UpdateWindowTitle()
        {
            if (ParentWindow == null)
                return;
            string? title = ViewModel.ActiveTab?.Title;
            ParentWindow.Title = string.IsNullOrEmpty(title)
                ? "Orion Browser"
                : $"{title} — Orion Browser";
        }

        /// <summary>
        /// Creates a new browser tab in response to a user gesture (Ctrl+T, the +
        /// button) and focuses the address bar so the user can start typing a URL.
        /// Deferred so the focus lands after the tab activation/strip sync settles.
        /// </summary>
        private void CreateNewTab(string url = "about:blank")
        {
            if (ViewModel.CreateNewTab(url) != null)
                _ = DispatcherQueue.TryEnqueue(() => _ = FocusAddressBar());
        }

        /// <summary>
        /// Opens a link diverted out of its originating view (modifier-clicked
        /// link or engine-computed disposition) as a new standalone tab or
        /// window. Background-gesture tabs are focused only when
        /// settings::tabs::when_opening_link_in_new_tab_make_it_active is on;
        /// settings::tabs::open_new_tab_next_to_current_tab picks the position.
        /// </summary>
        internal void OpenLink(string url, OrionBackend.OpenDisposition disposition, string? title = null)
        {
            if (disposition == OrionBackend.OpenDisposition.NewWindow)
            {
                OpenLinkInNewWindow(url, title);
                return;
            }

            bool makeActive = OrionBackend.GetBoolSetting(ViewModel.ActiveProfileId,
                "settings::tabs::when_opening_link_in_new_tab_make_it_active", "settings::tabs",
                "when_opening_link_in_new_tab_make_it_active", false);
            bool nextToCurrent = OrionBackend.GetBoolSetting(ViewModel.ActiveProfileId,
                "settings::tabs::open_new_tab_next_to_current_tab", "settings::tabs",
                "open_new_tab_next_to_current_tab", false);

            if (disposition == OrionBackend.OpenDisposition.NewForegroundTab || makeActive)
                _ = ViewModel.CreateNewTab(url, title, insertAfterActive: nextToCurrent);
            else
                OpenNewBackgroundTab(url, insertAfterActive: nextToCurrent, title);
        }

        private void OpenLinkInNewWindow(string url, string? title = null)
        {
            var newWindow = new MainWindow(ViewModel.ActiveProfile);
            newWindow.Activate();
            MainWindowViewModel vm = newWindow.MainWindowContent.ViewModel;
            // Replace the default blank tab created by the constructor
            BrowserTab? blankTab = vm.ActiveTab;
            _ = vm.CreateNewTab(url, title);
            if (blankTab != null)
                _ = vm.CloseTab(blankTab);
        }

        /// <summary>
        /// Switches to a different tab
        /// </summary>
        private void SwitchToTab(BrowserTab tab) => ViewModel.SwitchToTab(tab);

        /// <summary>
        /// Switches to the next or previous tab in the active group, wrapping around.
        /// </summary>
        private void SwitchToAdjacentTab(bool forward)
        {
            if (ViewModel.ActiveGroup is not { } activeGroup)
                return;

            FlattenedTabCollection tabs = activeGroup.Model.FlattenedTabCollection;
            int count = tabs.Count;
            if (count <= 1)
                return;

            int current = activeGroup.ActiveTab != null ? tabs.IndexOf(activeGroup.ActiveTab) : 0;
            int next = forward
                ? (current + 1) % count
                : (current - 1 + count) % count;
            ViewModel.SwitchToTab(tabs[next]);
        }

        /// <summary>
        /// Ctrl+1..9: switches to the Nth displayed tab when
        /// settings::tabs::use_ctrl_1_to_9_to_switch_tabs is on, else opens the
        /// Nth bookmarks bar bookmark in the current tab.
        /// </summary>
        private void HandleCtrlNumberShortcut(int num)
        {
            bool switchTabs = OrionBackend.GetBoolSetting(ViewModel.ActiveProfileId,
                "settings::tabs::use_ctrl_1_to_9_to_switch_tabs", "settings::tabs",
                "use_ctrl_1_to_9_to_switch_tabs", true);

            if (switchTabs)
                SwitchToTabByNumber(num);
            else
                OpenBookmarkBarBookmarkByNumber(num);
        }

        private void SwitchToTabByNumber(int num)
        {
            if (ViewModel.ActiveGroup is not { } activeGroup)
                return;

            FlattenedTabCollection tabs = activeGroup.Model.FlattenedTabCollection;
            int count = tabs.Count;
            if (count == 0)
                return;

            // 9 means the last tab; 1..8 past the end clamp to the last tab
            int index = num == 9 ? count - 1 : Math.Min(num - 1, count - 1);
            ViewModel.SwitchToTab(tabs[index]);
        }

        private void OpenBookmarkBarBookmarkByNumber(int num)
        {
            List<BookmarkTreeNode> bookmarks = [.. GetBookmarkBarNodes().Where(n => !n.IsFolder)];
            if (bookmarks.Count == 0)
                return;

            BookmarkTreeNode bookmark = bookmarks[Math.Min(num - 1, bookmarks.Count - 1)];
            if (!string.IsNullOrEmpty(bookmark.Url))
                NavigateCurrentTabToBookmark(bookmark.Url, bookmark.Title);
        }

        /// <summary>
        /// Closes a tab. If the last tab was closed, closes the window.
        /// </summary>
        private void CloseTab(BrowserTab tab)
        {
            _suppressTabStripSelection = true;
            try
            {
                if (ViewModel.CloseTab(tab, out BrowserTab? navigateToStartPage))
                    ParentWindow?.Close();
                else if (navigateToStartPage != null)
                    NavigateToUrl(BrowserTab.StartPageUrl);
            }
            finally
            {
                _suppressTabStripSelection = false;
            }
            SyncTabStripSelection();
        }

        private void VerticalTabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BrowserTab tab)
                CloseTab(tab);
        }

        private void VerticalTab_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed
                && sender is TreeViewItem item && item.DataContext is BrowserTab tab
                && !tab.IsPinned)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        }

        private void VerticalTabTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is BrowserTab tab)
                SwitchToTab(tab);
        }


        /// <summary>
        /// Builds a context menu flyout for a tab with standard browser actions.
        /// </summary>
        private MenuFlyout BuildTabContextMenu(BrowserTab tab, MenuFlyout? flyout = null)
        {
            flyout ??= new MenuFlyout();

            var bookmark = new MenuFlyoutItem { Text = "Bookmark Tab" };
            bookmark.Click += (s, e) => _ = ShowAddBookmarkDialog(tab);
            flyout.Items.Add(bookmark);

            var duplicate = new MenuFlyoutItem { Text = "Duplicate Tab" };
            duplicate.Click += (s, e) => ViewModel.DuplicateTab(tab);
            flyout.Items.Add(duplicate);

            var copyLink = new MenuFlyoutItem { Text = "Copy Link" };
            copyLink.Click += (s, e) =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(tab.Url);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            };
            flyout.Items.Add(copyLink);

            var moveTab = new MenuFlyoutSubItem { Text = "Move Tab" };
            foreach (TabGroupViewModel groupVm in ViewModel.TabGroups)
            {
                if (groupVm == ViewModel.ActiveGroup || tab.Pin?.Scope == TabPinScope.Profile
                    || (tab.IsPinned && (!groupVm.Model.IsSaved || groupVm.Model.IsPrivate)))
                    continue;
                TabGroup moveTarget = groupVm.Model;
                var groupItem = new MenuFlyoutItem { Text = moveTarget.DisplayName };
                groupItem.Click += (s, e) =>
                {
                    _suppressTabStripSelection = true;
                    try { ViewModel.MoveTabToGroup(tab, moveTarget); }
                    finally { _suppressTabStripSelection = false; }
                    SyncTabStripSelection();
                };
                moveTab.Items.Add(groupItem);
            }
            if (moveTab.Items.Count == 0 && !tab.IsPinned)
                moveTab.Items.Add(new MenuFlyoutItem { Text = "(No other groups)", IsEnabled = false });
            if (moveTab.Items.Count > 0)
                flyout.Items.Add(moveTab);

            flyout.Items.Add(new MenuFlyoutSeparator());

            if (ViewModel.ActiveGroup?.Model is { IsPrivate: false } workspace)
                AddTabPinActions(flyout, tab, workspace);

            var reload = new MenuFlyoutItem { Text = "Reload Tab" };
            BrowserTabViewModel? tabVm = ViewModel.FindTabViewModel(tab);
            reload.IsEnabled = tabVm?.HasNativeWebView ?? false;
            reload.Click += (s, e) => tabVm?.ReloadCommand.Execute(null);
            flyout.Items.Add(reload);

            var suspend = new MenuFlyoutItem { Text = "Suspend Tab", IsEnabled = false };
            flyout.Items.Add(suspend);

            var suspendOthers = new MenuFlyoutItem { Text = "Suspend Other Tabs", IsEnabled = false };
            flyout.Items.Add(suspendOthers);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var close = new MenuFlyoutItem { Text = "Close Tab" };
            close.Click += (s, e) => CloseTab(tab);
            flyout.Items.Add(close);

            BrowserTab[] allTabs = ViewModel.ActiveGroup?.Model.FlattenedTabs.ToArray() ?? [];
            int tabIndex = Array.IndexOf(allTabs, tab);

            var closeOthers = new MenuFlyoutItem
            {
                Text = "Close Other Tabs",
                IsEnabled = allTabs.Length > 1
            };
            closeOthers.Click += (s, e) =>
            {
                _suppressTabStripSelection = true;
                try { ViewModel.CloseOtherTabs(tab); }
                finally { _suppressTabStripSelection = false; }
                SyncTabStripSelection();
            };
            flyout.Items.Add(closeOthers);

            var closeRight = new MenuFlyoutItem
            {
                Text = ViewModel.TabLayout != TabLayout.vertical ? "Close Tabs to the Right" : "Close Tabs Below",
                IsEnabled = tabIndex >= 0 && tabIndex < allTabs.Length - 1
            };
            closeRight.Click += (s, e) =>
            {
                _suppressTabStripSelection = true;
                try { ViewModel.CloseTabsToTheRight(tab); }
                finally { _suppressTabStripSelection = false; }
                SyncTabStripSelection();
            };
            flyout.Items.Add(closeRight);

            return flyout;
        }

        private void AddTabPinActions(MenuFlyout flyout, BrowserTab tab, TabGroup workspace)
        {
            const string PinScopeMenuGroup = "Tab pin scope";
            var pinHere = new RadioMenuFlyoutItem
            {
                Text = workspace.IsSaved ? "Pin to this workspace" : "Pin to this workspace (save workspace first)",
                GroupName = PinScopeMenuGroup,
                IsChecked = tab.Pin?.Scope == TabPinScope.Workspace,
                IsEnabled = workspace.IsSaved,
            };
            pinHere.Click += (_, _) => SetTabPinScope(tab, TabPinScope.Workspace);
            flyout.Items.Add(pinHere);

            var pinShared = new RadioMenuFlyoutItem
            {
                Text = "Pin across workspaces",
                GroupName = PinScopeMenuGroup,
                IsChecked = tab.Pin?.Scope == TabPinScope.Profile,
            };
            pinShared.Click += (_, _) => SetTabPinScope(tab, TabPinScope.Profile);
            flyout.Items.Add(pinShared);

            if (tab.Pin is not { } pin)
                return;

            AddPinFolderMenu(flyout, tab, workspace);

            bool isOnSavedPage = tab.Url == pin.Url;
            var reset = new MenuFlyoutItem { Text = "Reset to saved page", IsEnabled = !isOnSavedPage };
            reset.Click += (_, _) =>
            {
                SwitchToTab(tab);
                if (ViewModel.ActiveTab == tab && tab.Pin is { } savedPage)
                    NavigateToUrl(savedPage.Url);
            };
            flyout.Items.Add(reset);

            var savePage = new MenuFlyoutItem { Text = "Use current page as saved page", IsEnabled = !isOnSavedPage };
            savePage.Click += (_, _) =>
            {
                if (!ViewModel.UseCurrentPageAsPin(tab))
                    _ = PopupDialog.ShowMessageAsync(this, "Couldn’t update the pin", "Try saving the page again.", closeButton: "OK");
            };
            flyout.Items.Add(savePage);

            var unpin = new MenuFlyoutItem { Text = "Unpin tab" };
            unpin.Click += (_, _) => SetTabPinScope(tab, null);
            flyout.Items.Add(unpin);
        }

        private void SetTabPinScope(BrowserTab tab, TabPinScope? scope)
        {
            _suppressTabStripSelection = true;
            try
            {
                if (!ViewModel.SetTabPinScope(tab, scope))
                    _ = PopupDialog.ShowMessageAsync(this, "Couldn’t update the pin", "Try changing the pin again.", closeButton: "OK");
            }
            finally
            {
                _suppressTabStripSelection = false;
                SyncTabStripSelection();
            }
        }

        private void VerticalTab_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs args)
        {
            if (sender is not TreeViewItem treeItem || treeItem.DataContext is not BrowserTab tab)
                return;

            HideTabHoverPreview();
            MenuFlyout flyout = BuildTabContextMenu(tab);
            flyout.ShowAt(treeItem, new FlyoutShowOptions
            {
                Position = args.GetPosition(treeItem)
            });
        }

        private void HorizontalTab_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs args)
        {
            if (sender is not FrameworkElement element || element.Tag is not BrowserTab tab)
                return;

            HideTabHoverPreview();
            MenuFlyout flyout = BuildTabContextMenu(tab);
            flyout.ShowAt(element, new FlyoutShowOptions
            {
                Position = args.GetPosition(element)
            });
        }

        private void VerticalTabStrip_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
        {
            ViewModel.ActiveGroup?.Model.EnforcePinnedConstraints();
            ViewModel.ActiveGroup?.Model.NotifyTreeChanged();

            // TreeView can restructure parent-child relationships. Use
            // is_nested=true so the core moves descendants atomically.
            foreach (object item in args.Items)
            {
                if (item is BrowserTab tab)
                    PersistTabMove(tab, isNested: true);
            }
        }

        private void OnHorizontalTabReordered(BrowserTab tab) => PersistTabMove(tab);

        /// <summary>
        /// Persists a tab's new position after a drag-drop reorder via the core's
        /// move_tab API.
        /// </summary>
        private void PersistTabMove(BrowserTab tab, bool isNested = false)
        {
            if (tab.Id == 0 || ViewModel.ActiveGroup is not { } activeGroup)
                return;

            TabGroup group = activeGroup.Model;
            if (!group.TabExists(tab))
                return;
            BrowserTab? parent = group.FindParentTab(tab);
            ObservableCollection<BrowserTab> siblings = parent?.Children ?? group.Tabs;
            int index = siblings.IndexOf(tab);
            ViewModel.PersistTabReorder(tab, group, parent, siblings.Skip(index + 1), isNested);
        }

        private void NewTab_Click(object sender, RoutedEventArgs e) => CreateNewTab(ViewModel.ResolveNewTabUrl());

        private void TabStrip_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is BrowserTab tab)
                CloseTab(tab);
        }

        /// <summary>
        /// Pushes the ViewModel's ActiveTab into the visible TabView after a
        /// guarded collection mutation (the guard suppressed auto-selection).
        /// </summary>
        private void SyncTabStripSelection()
        {
            if (ViewModel.ActiveTab is not { } activeTab)
                return;
            if (ViewModel.TabLayout == TabLayout.standard)
                HorizontalTabStrip.SelectedItem = activeTab;
            else if (ViewModel.TabLayout == TabLayout.compact)
                CompactTabStrip.SelectedItem = activeTab;
        }

        private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTabStripSelection)
                return;
            if (sender is TabView tabView && tabView.SelectedItem is BrowserTab tab)
            {
                if (ViewModel.ActiveTab != tab)
                    SwitchToTab(tab);
            }
        }


        private void TabOverviewButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTabOverview();
        }

        /// <summary>
        /// Opens a settings tab, or switches to an existing one.
        /// </summary>
        internal void OpenSettingsTab(string? category = null)
        {
            // Look for an existing settings tab
            BrowserTab? settingsTab = ViewModel.ActiveGroup?.Model.FlattenedTabs
                .FirstOrDefault(t => t.Url == "orion://settings");

            if (settingsTab != null)
                SwitchToTab(settingsTab);
            else
                _ = ViewModel.CreateNewTab("orion://settings");

            if (category != null)
                SettingsPageContent.SelectCategory(category);
        }

        /// <summary>
        /// Opens a downloads tab, or switches to an existing one.
        /// </summary>
        private void OpenDownloadsTab()
        {
            BrowserTab? downloadsTab = ViewModel.ActiveGroup?.Model.FlattenedTabs
                .FirstOrDefault(t => t.Url == "orion://downloads");

            if (downloadsTab != null)
            {
                SwitchToTab(downloadsTab);
            }
            else
            {
                if (ViewModel.CreateNewTab("orion://downloads") != null)
                    NavigateToUrl("orion://downloads");
            }
        }
    }
}
