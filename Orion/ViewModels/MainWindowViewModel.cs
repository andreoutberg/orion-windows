using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace Orion
{
    public static class SidebarPageExtensions
    {
        public static string DisplayName(this SidebarPage page) => page switch
        {
            SidebarPage.not_set => page.ToString(),
            SidebarPage.windows => "Tab Groups",
            SidebarPage.tabs => "Tabs",
            SidebarPage.bookmarks => "Bookmarks",
            SidebarPage.reading_list => "Reading List",
            _ => page.ToString(),
        };

        public static string Glyph(this SidebarPage page) => page switch
        {
            SidebarPage.not_set => "\uE8A5",
            SidebarPage.windows => "\uE8B7",
            SidebarPage.tabs => "\uE7C3",
            SidebarPage.bookmarks => "\uE734",
            SidebarPage.reading_list => "\uE736",
            _ => "\uE8A5",
        };
    }

    public enum ProfileDisplayMode { IconOnly = 1, NameOnly = 2, IconAndName = 3 }

    /// <summary>
    /// ViewModel for MainWindow managing tab groups and tabs.
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject
    {
        /// <summary>
        /// Shared saved TabGroup models across all windows, keyed by profile ID.
        /// When a group is added/removed, all windows with the same profile react
        /// by creating/removing their own TabGroupViewModel wrappers with independent
        /// ActiveTab state.
        /// </summary>
        private static readonly Dictionary<long, ObservableCollection<TabGroup>> _sharedSavedGroupsByProfile = [];

        /// <summary>
        /// Suppresses OnSharedSavedGroupsChanged during bulk loading.
        /// </summary>
        private bool _suppressSharedSync;

        /// <summary>
        /// Returns the shared saved groups collection for a given profile,
        /// creating it on first access.
        /// </summary>
        private static ObservableCollection<TabGroup> SharedSavedGroups(long profileId)
        {
            if (!_sharedSavedGroupsByProfile.TryGetValue(profileId, out ObservableCollection<TabGroup>? groups))
            {
                groups = [];
                _sharedSavedGroupsByProfile[profileId] = groups;
            }
            return groups;
        }

        /// <summary>
        /// Per-instance accessor for the current profile's shared saved groups.
        /// </summary>
        private ObservableCollection<TabGroup> SharedSavedGroupsForProfile => SharedSavedGroups(_profileId);

        /// <summary>
        /// Resets shared state between unit tests.
        /// </summary>
        internal static void ResetSharedState()
        {
            _sharedSavedGroupsByProfile.Clear();
            s_sharedPinsByProfile.Clear();
        }

        /// <summary>
        /// Per-window BrowserTabViewModel wrappers, keyed by model.
        /// Each window has its own NativeWebView (WebKit HWND) per tab.
        /// </summary>
        private readonly Dictionary<BrowserTab, BrowserTabViewModel> _tabViewModels = [];

        private readonly long _profileId;

        /// <summary>
        /// Factory for creating NativeWebView instances. Set by the View,
        /// which captures window-specific context (parent HWND, event wiring).
        /// </summary>
        public Func<BrowserTab, NativeWebView>? WebViewFactory { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HorizontalTabsVisibility))]
        [NotifyPropertyChangedFor(nameof(CompactTabsVisibility))]
        public partial TabLayout TabLayout { get; set; } = TabLayout.standard;

        partial void OnTabLayoutChanged(TabLayout value) =>
            OrionBackend.SetIntSetting(ActiveProfileId, "settings::tabs::tab_layout", "settings::tabs",
                "tab_layout", (int)value, (int)TabLayout.standard);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BookmarkBarVisibility))]
        public partial bool ShowBookmarkBar { get; set; }

        partial void OnShowBookmarkBarChanged(bool value) =>
            OrionBackend.SetBoolSetting(ActiveProfileId, "settings::appearance::show_bookmarks_bar", "settings::appearance",
                "show_bookmarks_bar", value, true);

        public Profile ActiveProfile
        {
            get;
            internal set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ProfileIconVisibility));
                    OnPropertyChanged(nameof(ProfileNameVisibility));
                    OnPropertyChanged(nameof(ActiveProfileDisplayIcon));
                    OnPropertyChanged(nameof(ActiveProfileIconForeground));
                    OnPropertyChanged(nameof(ActiveProfileName));
                }
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProfileIconVisibility))]
        [NotifyPropertyChangedFor(nameof(ProfileNameVisibility))]
        public partial ProfileDisplayMode ProfileDisplayMode { get; set; } = ProfileDisplayMode.IconOnly;

        partial void OnProfileDisplayModeChanged(ProfileDisplayMode value) =>
            OrionBackend.SetIntSetting(ActiveProfileId, "settings::appearance::profile_display_mode", "settings::appearance",
                "profile_display_mode", (int)value, (int)ProfileDisplayMode.IconOnly);

        public Visibility ProfileIconVisibility =>
            ProfileDisplayMode != ProfileDisplayMode.NameOnly ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ProfileNameVisibility =>
            ProfileDisplayMode != ProfileDisplayMode.IconOnly ? Visibility.Visible : Visibility.Collapsed;

        public string ActiveProfileDisplayIcon => ActiveProfile.DisplayIcon;

        public Brush ActiveProfileIconForeground => ActiveProfile.IconForeground;

        public string ActiveProfileName => ActiveProfile.Name;

        public long ActiveProfileId => ActiveProfile.Id;

        /// <summary>
        /// Reloads the active profile from the backend (e.g. after name/icon change in settings).
        /// </summary>
        public void RefreshActiveProfile()
        {
            long id = ActiveProfile.Id;
            List<Profile> profiles = OrionBackend.GetProfiles();
            // One profile per process: fall back to the process's own profile,
            // then the registry default.
            ActiveProfile = profiles.FirstOrDefault(p => p.Id == id)
                ?? profiles.FirstOrDefault(p => p.Id == OrionBackend.GetCurrentProfileId())
                ?? profiles.First(p => p.IsDefault);
        }

        [ObservableProperty]
        public partial bool ShowSidebar { get; set; }

        private bool _suppressSidebarPersist;

        partial void OnShowSidebarChanged(bool value)
        {
            if (_suppressSidebarPersist)
                return;
            OrionBackend.SetBoolSetting(ActiveProfileId, "settings::appearance::show_sidebar", "settings::appearance",
                "show_sidebar", value, false);
        }

        /// <summary>
        /// Sets ShowSidebar without persisting the setting (fullscreen auto-hide/restore).
        /// The TwoWay pane bindings write the same value back, which the generated
        /// setter's equality check drops, so no persist can slip through.
        /// </summary>
        public void SetShowSidebarTransient(bool value)
        {
            _suppressSidebarPersist = true;
            try { ShowSidebar = value; }
            finally { _suppressSidebarPersist = false; }
        }

        /// <summary>
        /// True while window fullscreen hides the top chrome (toolbar, tab strip,
        /// bookmark bar). Set by the View's fullscreen controller; never persisted.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TitleBarVisibility))]
        [NotifyPropertyChangedFor(nameof(HorizontalTabsVisibility))]
        [NotifyPropertyChangedFor(nameof(BookmarkBarVisibility))]
        public partial bool IsChromeHiddenForFullscreen { get; set; }

        public Visibility TitleBarVisibility =>
            IsChromeHiddenForFullscreen ? Visibility.Collapsed : Visibility.Visible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SidebarTabsModeVisibility))]
        [NotifyPropertyChangedFor(nameof(SidebarTabGroupsVisibility))]
        [NotifyPropertyChangedFor(nameof(SidebarBookmarksVisibility))]
        [NotifyPropertyChangedFor(nameof(SidebarReadingListVisibility))]
        [NotifyPropertyChangedFor(nameof(SidebarHeaderText))]
        [NotifyPropertyChangedFor(nameof(NewTabSidebarButtonVisibility))]
        public partial SidebarPage SelectedSidebarPage { get; set; } = SidebarPage.windows;

        partial void OnSelectedSidebarPageChanged(SidebarPage value) =>
            OrionBackend.SetIntSetting(ActiveProfileId, "settings::appearance::sidebar_page", "settings::appearance",
                "sidebar_page", (int)value, (int)SidebarPage.windows);

        public MainWindowViewModel(Profile profile,
            List<OrionBackend.PersistedTabGroup>? persistedGroups = null)
        {
            ActiveProfile = profile;
            _profileId = profile.Id;
            _sharedPins = GetSharedPins(_profileId);
            TabGroups = [];
            TabGroups.CollectionChanged += OnTabGroupViewModelsCollectionChanged;

            if (persistedGroups != null && persistedGroups.Count > 0)
            {
                RestoreTabGroups(persistedGroups);
            }
            else
            {
                // First run or no persisted state — create an unsaved window group + tab
                long id = OrionBackend.CreateTabGroupEntry(_profileId,
                    isTabGroup: true, hasUiWindow: true);
                _ = CreateTabGroupFromDb(id);
                _ = CreateNewTab(ResolveNewWindowUrl());
            }

            // Saved tab groups are shared across all windows for a profile.
            // Load any that weren't already included in the persisted set.
            LoadSavedTabGroups();

            // React when other windows add/remove shared saved groups.
            SharedSavedGroupsForProfile.CollectionChanged += OnSharedSavedGroupsChanged;
            _sharedPins.Changed += OnSharedPinsChanged;
        }

        private void RestoreTabGroups(List<OrionBackend.PersistedTabGroup> groups)
        {
            foreach (OrionBackend.PersistedTabGroup g in groups)
                _ = RestoreSingleGroup(g);

            // Ensure at least one group with one tab
            if (TabGroups.Count == 0)
            {
                long id = OrionBackend.CreateTabGroupEntry(_profileId,
                    isTabGroup: true, hasUiWindow: true);
                _ = CreateTabGroupFromDb(id);
                _ = CreateNewTab(ResolveNewWindowUrl());
            }
            else if (ActiveGroup != null && !ActiveGroup.Model.FlattenedTabs.Any())
            {
                _ = CreateNewTab(ResolveNewWindowUrl());
            }
        }

        private TabGroupViewModel RestoreSingleGroup(OrionBackend.PersistedTabGroup g)
        {
            bool isSaved = !g.IsTabGroup;

            // Saved groups share the same model across windows
            if (isSaved)
            {
                TabGroup? shared = SharedSavedGroupsForProfile.FirstOrDefault(g2 => g2.Id == g.Id);
                if (shared != null)
                {
                    var vm = new TabGroupViewModel(shared);
                    TabGroups.Add(vm);

                    // Restore per-window active tab
                    if (g.SelectedTabId != 0)
                    {
                        BrowserTab? selected = shared.FlattenedTabs
                            .FirstOrDefault(t => t.Id == g.SelectedTabId);
                        vm.ActiveTab = selected ?? shared.FlattenedTabs.FirstOrDefault();
                    }
                    else if (shared.FlattenedTabs.Any())
                    {
                        vm.ActiveTab = shared.FlattenedTabs.First();
                    }

                    return vm;
                }
            }

            // New model — build from DB
            TabGroupViewModel newVm = CreateTabGroupFromDb(g.Id, g.Name, isSaved, g.KeyboardShortcut, g.IsPrivate);
            TabGroup group = newVm.Model;

            List<OrionBackend.PersistedTab> tabs = OrionBackend.GetTabs(g.Id);
            // Build flat tab list first, then reconstruct tree
            var tabMap = new Dictionary<long, BrowserTab>();
            foreach (OrionBackend.PersistedTab t in tabs)
            {
                tabMap[t.Id] = RestoreTab(t, g.Id);
            }

            // Reconstruct parent-child tree (core returns tabs pre-sorted)
            foreach (OrionBackend.PersistedTab t in tabs)
            {
                BrowserTab tab = tabMap[t.Id];
                if (t.ParentTabId != 0 && tabMap.TryGetValue(t.ParentTabId, out BrowserTab? parent))
                    parent.Children.Add(tab);
                else
                    group.Tabs.Add(tab);
            }
            group.EnforcePinnedConstraints();
            RestorePinFolders(group);
            group.NotifyTreeChanged();

            // Restore selected tab
            newVm.ActiveTab = group.FlattenedTabs.FirstOrDefault(t => t.Id == g.SelectedTabId)
                ?? tabMap.Values.FirstOrDefault() ?? group.Tabs.FirstOrDefault();

            foreach (BrowserTab tab in tabMap.Values)
                _ = LoadCachedFaviconAsync(tab);

            return newVm;
        }

        /// <summary>
        /// Loads saved tab groups into this window. On first call, populates the
        /// shared collection from the DB. Subsequent windows reuse the shared models.
        /// </summary>
        private void LoadSavedTabGroups()
        {
            _suppressSharedSync = true;
            try
            {
                // Populate shared collection from DB for any saved groups not yet loaded
                var sharedIds = new HashSet<long>(SharedSavedGroupsForProfile.Select(g => g.Id));
                var dbSaved = OrionBackend.GetTabGroups(_profileId)
                    .Where(g => !g.IsTabGroup && !sharedIds.Contains(g.Id))
                    .ToList();
                foreach (OrionBackend.PersistedTabGroup g in dbSaved)
                    _ = RestoreSingleGroup(g);

                // Wrap any shared groups not yet in this window
                var loadedIds = new HashSet<long>(TabGroups.Select(vm => vm.Model.Id));
                TabGroupViewModel? previousActive = ActiveGroup;

                foreach (TabGroup shared in SharedSavedGroupsForProfile)
                {
                    if (!loadedIds.Contains(shared.Id))
                        _ = WrapSharedGroup(shared);
                }

                if (previousActive != null)
                    ActiveGroup = previousActive;
            }
            finally
            {
                _suppressSharedSync = false;
            }
        }

        /// <summary>
        /// Creates a per-window VM wrapper for a shared saved group model.
        /// </summary>
        private TabGroupViewModel WrapSharedGroup(TabGroup shared)
        {
            var vm = new TabGroupViewModel(shared);
            TabGroups.Add(vm);
            if (shared.FlattenedTabs.Any())
                vm.ActiveTab = shared.FlattenedTabs.First();
            return vm;
        }

        private void OnSharedSavedGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressSharedSync)
                return;

            if (e.NewItems != null)
            {
                foreach (TabGroup group in e.NewItems)
                {
                    // Skip if this window already has a VM for this group
                    // (the window that created the group already added it)
                    if (FindViewModel(group) != null)
                        continue;

                    TabGroupViewModel? previousActive = ActiveGroup;
                    _ = WrapSharedGroup(group);
                    if (previousActive != null)
                        ActiveGroup = previousActive;
                }
            }
            if (e.OldItems != null)
            {
                foreach (TabGroup group in e.OldItems)
                {
                    TabGroupViewModel? vm = FindViewModel(group);
                    if (vm == null)
                        continue;

                    _ = TabGroups.Remove(vm);
                    if (ActiveGroup == vm)
                        ActiveGroup = TabGroups.FirstOrDefault();
                }
            }
        }

        /// <summary>
        /// Creates a TabGroup backed by an existing DB ID, wraps it in a ViewModel.
        /// </summary>
        private TabGroupViewModel CreateTabGroupFromDb(long tabGroupId, string name = "",
            bool isSaved = false, string keyboardShortcut = "", bool? isPrivate = null)
        {
            TabGroup group = new()
            {
                Id = tabGroupId,
                Name = name,
                IsSaved = isSaved,
                IsPrivate = isPrivate ?? OrionBackend.IsPrivateSession,
                Order = TabGroups.Count,
                KeyboardShortcut = keyboardShortcut,
            };
            var vm = new TabGroupViewModel(group);
            TabGroups.Add(vm);
            SelectTabGroup(group);
            return vm;
        }

        /// <summary>
        /// All tab groups.
        /// </summary>
        public ObservableCollection<TabGroupViewModel> TabGroups { get; }

        /// <summary>
        /// The single unsaved (unnamed) tab group model, if any. Per-window.
        /// </summary>
        [ObservableProperty]
        public partial TabGroup? UnsavedTabGroup { get; private set; }

        /// <summary>
        /// Saved tab groups shared across all windows.
        /// </summary>
        public ObservableCollection<TabGroup> SavedTabGroups => SharedSavedGroupsForProfile;

        /// <summary>
        /// Finds the ViewModel wrapper for a given TabGroup model.
        /// </summary>
        private TabGroupViewModel? FindViewModel(TabGroup group) => TabGroups.FirstOrDefault(vm => vm.Model == group);

        /// <summary>
        /// Gets or creates a per-window BrowserTabViewModel for the given tab.
        /// </summary>
        public BrowserTabViewModel GetOrCreateTabViewModel(BrowserTab tab)
        {
            if (!_tabViewModels.TryGetValue(tab, out BrowserTabViewModel? vm))
            {
                vm = new BrowserTabViewModel(tab);
                _tabViewModels[tab] = vm;
            }
            return vm;
        }

        /// <summary>
        /// Finds the per-window BrowserTabViewModel for a tab, if it exists.
        /// </summary>
        public BrowserTabViewModel? FindTabViewModel(BrowserTab tab) => _tabViewModels.GetValueOrDefault(tab);

        private void DisposeTabViewModel(BrowserTab tab)
        {
            if (_tabViewModels.TryGetValue(tab, out BrowserTabViewModel? vm))
            {
                vm.NativeWebView?.Dispose();
                vm.NativeWebView = null;
                _ = _tabViewModels.Remove(tab);
            }
        }

        /// <summary>
        /// Currently active/selected tab group.
        /// </summary>
        public TabGroupViewModel? ActiveGroup
        {
            get;
            private set
            {
                TabGroupViewModel? oldGroup = field;
                if (SetProperty(ref field, value))
                {
                    if (oldGroup != null)
                    {
                        oldGroup.IsSelected = false;
                        oldGroup.PropertyChanged -= OnActiveGroupPropertyChanged;
                    }
                    if (value != null)
                    {
                        value.IsSelected = true;
                        value.PropertyChanged += OnActiveGroupPropertyChanged;
                    }
                    OnPropertyChanged(nameof(ActiveGroupTabs));
                    OnPropertyChanged(nameof(FlattenedActiveGroupTabs));
                    OnPropertyChanged(nameof(ActiveGroupName));
                    OnPropertyChanged(nameof(SidebarHeaderText));
                    OnPropertyChanged(nameof(ActiveTab));
                    OnPropertyChanged(nameof(ActiveTabViewModel));
                }
            }
        }

        /// <summary>
        /// The active tab in the active group.
        /// Changes are forwarded from TabGroupViewModel.ActiveTab via PropertyChanged.
        /// </summary>
        public BrowserTab? ActiveTab => ActiveGroup?.ActiveTab;

        /// <summary>
        /// Per-window ViewModel for the active tab, holding NativeWebView and commands.
        /// </summary>
        public BrowserTabViewModel? ActiveTabViewModel =>
            ActiveGroup?.ActiveTab is { } tab ? GetOrCreateTabViewModel(tab) : null;

        /// <summary>
        /// Tabs from the active group for binding.
        /// </summary>
        public ObservableCollection<BrowserTab>? ActiveGroupTabs => ActiveGroup?.Model.Tabs;

        /// <summary>
        /// Flattened tabs from the active group for horizontal tab binding.
        /// </summary>
        public FlattenedTabCollection? FlattenedActiveGroupTabs => ActiveGroup?.Model.FlattenedTabCollection;

        [ObservableProperty]
        public partial bool ShowNestedVerticalTabs { get; set; } = true;

        [ObservableProperty]
        public partial bool ShowFaviconsInTabs { get; set; } = true;

        partial void OnShowFaviconsInTabsChanged(bool value)
        {
            BrowserTab.GlobalShowFavicons = value;
            foreach (BrowserTab tab in ActiveGroup?.Model.FlattenedTabs ?? [])
                tab.NotifyFaviconVisibilityChanged();
        }

        [ObservableProperty]
        public partial bool ShowWebsiteTitlesInTabs { get; set; } = true;

        partial void OnShowWebsiteTitlesInTabsChanged(bool value)
        {
            BrowserTab.GlobalShowTitles = value;
            foreach (BrowserTab tab in ActiveGroup?.Model.FlattenedTabs ?? [])
                tab.NotifyTitleVisibilityChanged();
        }

        /// <summary>
        /// Display label for the active group (name if saved, tab count if unsaved).
        /// </summary>
        public string ActiveGroupName => ActiveGroup?.Model.DisplayName ?? string.Empty;

        /// <summary>
        /// Visibility for horizontal tab strip (hidden in vertical layout).
        /// </summary>
        public Visibility HorizontalTabsVisibility =>
            TabLayout == TabLayout.standard && !IsChromeHiddenForFullscreen ? Visibility.Visible : Visibility.Collapsed;

        public Visibility CompactTabsVisibility =>
            TabLayout == TabLayout.compact ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for the bookmark bar.
        /// </summary>
        public Visibility BookmarkBarVisibility =>
            ShowBookmarkBar && !IsChromeHiddenForFullscreen ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for vertical tab list in the sidebar (always visible when sidebar shows tabs).
        /// </summary>
        public Visibility VerticalTabsVisibility => Visibility.Visible;

        /// <summary>
        /// Visibility for vertical group headers in the sidebar (always visible).
        /// </summary>
        public Visibility VerticalGroupHeadersVisibility => Visibility.Visible;

        /// <summary>
        /// Visibility for new tab sidebar button (shown in Tabs sidebar mode).
        /// </summary>
        public Visibility NewTabSidebarButtonVisibility =>
            SelectedSidebarPage == SidebarPage.tabs ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for the tabs panel in the sidebar.
        /// </summary>
        public Visibility SidebarTabsModeVisibility =>
            SelectedSidebarPage == SidebarPage.tabs ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for the tab groups panel in the sidebar.
        /// </summary>
        public Visibility SidebarTabGroupsVisibility =>
            SelectedSidebarPage == SidebarPage.windows ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for the bookmarks panel in the sidebar.
        /// </summary>
        public Visibility SidebarBookmarksVisibility =>
            SelectedSidebarPage == SidebarPage.bookmarks ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for the reading list panel in the sidebar.
        /// </summary>
        public Visibility SidebarReadingListVisibility =>
            SelectedSidebarPage == SidebarPage.reading_list ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Header text for the sidebar, changes based on mode.
        /// </summary>
        public string SidebarHeaderText => SelectedSidebarPage == SidebarPage.tabs
            ? ActiveGroup?.Model.DisplayName ?? string.Empty
            : SelectedSidebarPage.DisplayName();


        /// <summary>
        /// Saves the unsaved tab group: creates a new saved group, moves all tabs
        /// into it, and leaves the unsaved group with a fresh new tab.
        /// </summary>
        /// <param name="name">Name for the new saved group.</param>
        /// <returns>The ViewModel for the newly saved group.</returns>
        public TabGroupViewModel SaveUnsavedGroup(string name)
        {
            if (UnsavedTabGroup == null)
                return CreateTabGroup();

            TabGroup unsaved = UnsavedTabGroup;
            TabGroupViewModel? unsavedVm = FindViewModel(unsaved);
            BrowserTab? previousActive = unsavedVm?.ActiveTab;

            // Create a new saved group in DB
            long newId = OrionBackend.CreateTabGroupEntry(_profileId,
                isTabGroup: false, hasUiWindow: true);
            OrionBackend.UpdateTabGroupName(newId, name);

            var savedGroup = new TabGroup
            {
                Id = newId,
                Name = name,
                IsSaved = true,
                IsPrivate = unsaved.IsPrivate,
                Order = TabGroups.Count,
            };
            var savedVm = new TabGroupViewModel(savedGroup);

            // Move all root tabs (children follow their parents) from unsaved to saved
            BrowserTab[] rootTabs = [.. unsaved.Tabs.Where(t => t.Pin?.Scope != TabPinScope.Profile)];
            foreach (BrowserTab tab in rootTabs)
            {
                _ = unsaved.Tabs.Remove(tab);
                savedGroup.Tabs.Add(tab);
            }

            // Update DB group ID for all tabs (including nested children)
            foreach (BrowserTab tab in savedGroup.FlattenedTabs)
            {
                tab.GroupId = newId;
                if (tab.Id != 0)
                    OrionBackend.UpdateTabGroupId(tab.Id, newId);
            }

            unsaved.NotifyTreeChanged();
            savedGroup.NotifyTreeChanged();

            // Add to TabGroups (triggers shared group sync)
            TabGroups.Add(savedVm);
            savedVm.ActiveTab = savedGroup.FlattenedTabs.FirstOrDefault(t => t.Id == previousActive?.Id)
                ?? savedGroup.FlattenedTabs.FirstOrDefault();

            // Create a fresh tab in the unsaved group
            ActiveGroup = unsavedVm;
            _ = CreateNewTab();

            // Switch to the saved group
            ActiveGroup = savedVm;

            return savedVm;
        }

        /// <summary>
        /// Duplicates a saved tab group: creates a new saved group with copies of all tabs.
        /// </summary>
        /// <param name="source">The group to duplicate.</param>
        /// <returns>The ViewModel for the new duplicated group.</returns>
        public TabGroupViewModel DuplicateTabGroup(TabGroup source)
        {
            string name = $"{source.Name} Copy";
            long newId = OrionBackend.CreateTabGroupEntry(_profileId,
                isTabGroup: false, hasUiWindow: true);
            OrionBackend.UpdateTabGroupName(newId, name);

            var dupGroup = new TabGroup
            {
                Id = newId,
                Name = name,
                IsSaved = true,
                IsPrivate = source.IsPrivate,
                Order = TabGroups.Count,
            };
            var dupVm = new TabGroupViewModel(dupGroup);

            // Deep-copy the tab tree
            foreach (BrowserTab rootTab in source.Tabs.Where(t => t.Pin?.Scope != TabPinScope.Profile))
                dupGroup.Tabs.Add(CopyTabTree(rootTab, newId));

            dupGroup.NotifyTreeChanged();
            dupVm.ActiveTab = dupGroup.FlattenedTabs.FirstOrDefault();

            TabGroups.Add(dupVm);
            ActiveGroup = dupVm;

            return dupVm;
        }

        /// <summary>
        /// Recursively copies a tab and its children, creating new DB entries.
        /// </summary>
        private BrowserTab CopyTabTree(BrowserTab source, long groupId)
        {
            string url = source.Pin?.Url ?? source.Url;
            string title = source.Pin?.Title ?? source.Title;
            long dbId = OrionBackend.CreateTab(groupId, url, title, source.IsPinned);
            var copy = new BrowserTab
            {
                Id = dbId,
                Title = title,
                Url = url,
                GroupId = groupId,
                IsHibernated = true,
                FaviconImage = source.FaviconImage,
            };
            copy.SetPin(source.Pin);
            foreach (BrowserTab child in source.Children)
                copy.Children.Add(CopyTabTree(child, groupId));
            return copy;
        }

        /// <summary>
        /// Creates a new tab group.
        /// </summary>
        /// <returns>The newly created tab group.</returns>
        public TabGroupViewModel CreateTabGroup(bool forceEmpty = false)
        {
            string name = GenerateTabGroupName();
            long id = OrionBackend.CreateTabGroupEntry(_profileId,
                isTabGroup: false, hasUiWindow: true);
            OrionBackend.UpdateTabGroupName(id, name);
            TabGroupViewModel vm = CreateTabGroupFromDb(id, name, isSaved: true);
            if (!forceEmpty)
                _ = CreateNewTab();
            return vm;
        }

        private string GenerateTabGroupName()
        {
            var existingNames = new HashSet<string>(
                TabGroups.Where(g => g.Model.IsSaved).Select(g => g.Model.Name));
            for (int i = 1; ; i++)
            {
                string name = $"Tab Group {i}";
                if (!existingNames.Contains(name))
                    return name;
            }
        }

        internal string ResolveNewTabUrl()
        {
            int setting = OrionBackend.GetIntSetting(_profileId,
                "settings::general::new_tabs_open_with", "settings::general", "new_tabs_open_with", 1);
            return ResolveOpenWithUrl(setting);
        }

        internal string ResolveNewWindowUrl()
        {
            int setting = OrionBackend.GetIntSetting(_profileId,
                "settings::general::new_windows_open_with", "settings::general", "new_windows_open_with", 1);
            return ResolveOpenWithUrl(setting);
        }

        private string ResolveOpenWithUrl(int setting) => setting switch
        {
            1 => BrowserTab.StartPageUrl,
            2 => "about:blank",
            3 => ActiveGroup?.ActiveTab?.Url ?? BrowserTab.StartPageUrl,
            4 => OrionBackend.GetStringSetting(_profileId,
                "settings::general::homepage", "settings::general", "homepage", "https://kagi.com/"),
            // 5 = notes — not yet implemented
            _ => BrowserTab.StartPageUrl,
        };

        private static string? InternalPageTitle(string url)
        {
            if (url.StartsWith("orion://newtab")) return "Start Page";
            if (url.StartsWith("orion://settings")) return "Settings";
            if (url.StartsWith("orion://downloads")) return "Downloads";
            return null;
        }

        /// <summary>
        /// Creates a new tab in the active group.
        /// </summary>
        /// <param name="url">Initial URL for the tab.</param>
        /// <returns>The newly created tab, or null if no active group.</returns>
        public BrowserTab? CreateNewTab(string url = "about:blank", string? title = null,
            bool insertAfterActive = false, bool activate = true)
        {
            if (ActiveGroup == null)
                return null;

            title ??= InternalPageTitle(url);

            TabGroup group = ActiveGroup.Model;
            long dbId = OrionBackend.CreateTab(group.Id, url, title ?? "", isPinned: false);
            BrowserTab tab = new()
            {
                Id = dbId,
                Url = url,
                GroupId = group.Id
            };

            if (title != null)
                tab.Title = title;

            BrowserTab? activeTab = insertAfterActive ? ActiveGroup.ActiveTab : null;
            if (activeTab != null)
                group.InsertTabAfter(tab, activeTab);
            else
                group.AddTab(tab);
            if (activate)
                ActiveGroup.ActiveTab = tab;

            return tab;
        }

        /// <summary>
        /// Switches to a specific tab.
        /// </summary>
        /// <param name="tab">The tab to switch to.</param>
        public void SwitchToTab(BrowserTab tab)
        {
            if (ActiveGroup == null)
                return;

            TabGroup group = ActiveGroup.Model;
            if (!group.TabExists(tab))
                return;

            ActiveGroup.ActiveTab = tab;
        }

        /// <summary>
        /// Switches to a tab by index.
        /// </summary>
        /// <param name="index">Index of the tab to switch to.</param>
        public void SwitchToTabByIndex(int index)
        {
            if (ActiveGroup == null || index < 0 || index >= ActiveGroup.Model.Tabs.Count)
                return;

            SwitchToTab(ActiveGroup.Model.Tabs[index]);
        }

        /// <summary>
        /// Closes a specific tab.
        /// </summary>
        /// <returns>True if the last tab was closed and the window should close.</returns>
        public bool CloseTab(BrowserTab tab) => CloseTab(tab, out _);

        /// <summary>
        /// Closes a specific tab.
        /// </summary>
        /// <param name="tab">The tab to close.</param>
        /// <param name="navigateToStartPage">Set to the tab when it should be navigated to
        /// the start page instead of closed (preserve-tab-tree behavior).</param>
        /// <returns>True if the last tab was closed and the window should close.</returns>
        public bool CloseTab(BrowserTab tab, out BrowserTab? navigateToStartPage)
        {
            navigateToStartPage = null;

            if (ActiveGroup == null
                || (tab.IsPinned && OrionBackend.GetBoolSetting(_profileId,
                    "settings::tabs::prevent_closing_pinned_tabs", "settings::tabs", "prevent_closing_pinned_tabs", true)))
            {
                return false;
            }

            TabGroup group = ActiveGroup.Model;
            if (tab.Pin?.Scope == TabPinScope.Profile)
                _ = group.DismissedSharedPins.Add(tab.Id);

            bool returnToLastActive = OrionBackend.GetBoolSetting(_profileId,
                "settings::tabs::return_to_last_active_tab_when_closing_current_tab", "settings::tabs",
                "return_to_last_active_tab_when_closing_current_tab", true);

            if (tab.Children.Count > 0)
            {
                int closingBehavior = OrionBackend.GetIntSetting(_profileId,
                    "settings::tabs::closing_expanded_tab_behavior", "settings::tabs", "closing_expanded_tab_behavior", 0);

                // Convert first child: first child takes parent's place, siblings nest under it
                if (closingBehavior == 1)
                {
                    BrowserTab firstChild = tab.Children[0];
                    group.RemoveTabConvertFirstChild(tab);
                    if (ActiveGroup.ActiveTab == tab)
                        ActiveGroup.ActiveTab = firstChild;
                }
                // Preserve tab tree: navigate parent to start page instead of closing
                else if (closingBehavior == 2 && !BrowserTab.IsStartPageUrl(tab.Url))
                {
                    navigateToStartPage = tab;
                    return false;
                }
                else
                {
                    ActiveGroup.RemoveTab(tab, returnToLastActive);
                }
            }
            else
            {
                ActiveGroup.RemoveTab(tab, returnToLastActive);
            }

            if (tab.Id != 0 && !tab.IsPinned)
            {
                OrionBackend.CreateUndoRedoTabStep(group.Id, [tab.Id]);
                OrionBackend.DeleteTabs([tab.Id]);
            }

            if (!tab.IsPinned && !string.IsNullOrEmpty(tab.Url) && tab.Url != "about:blank")
                OrionBackend.CreateRecentlyClosedTab(_profileId, tab.Title, tab.Url);

            DisposeTabViewModel(tab);

            bool onlyPinnedRemain = group.Tabs.Count > 0 && group.Tabs.All(t => t.IsPinned);
            bool noTabsRemain = group.Tabs.Count == 0;

            if (noTabsRemain || onlyPinnedRemain)
            {
                bool closeWindow = OrionBackend.GetBoolSetting(_profileId,
                    "settings::tabs::close_window_with_last_tab", "settings::tabs", "close_window_with_last_tab", true);

                bool closeWhenPinned = closeWindow && onlyPinnedRemain && OrionBackend.GetBoolSetting(_profileId,
                    "settings::tabs::close_window_with_last_tab_when_pinned_tabs_present", "settings::tabs",
                    "close_window_with_last_tab_when_pinned_tabs_present", false);

                bool shouldClose = closeWindow && (noTabsRemain || closeWhenPinned);

                if (!shouldClose)
                    return false;

                bool hasOtherUnsavedGroups = TabGroups.Any(
                    vm => vm.Model != group && !vm.Model.IsSaved);
                if (!hasOtherUnsavedGroups)
                {
                    ActiveGroup = null;
                    _ = DeleteTabGroup(group);
                    return true;
                }

                _ = DeleteTabGroup(group);
            }

            return false;
        }

        /// <summary>
        /// Duplicates a tab by creating a new tab with the same URL and title.
        /// </summary>
        public BrowserTab? DuplicateTab(BrowserTab tab) => CreateNewTab(tab.Url, tab.Title);

        /// <summary>
        /// Maps a modifier-clicked bookmark activation to its open disposition,
        /// following Windows link-gesture conventions: Ctrl+Shift = foreground tab,
        /// Ctrl = background tab, Shift = window. Null for an unmodified activation.
        /// </summary>
        public static OrionBackend.OpenDisposition? BookmarkGestureDisposition(VirtualKeyModifiers modifiers)
        {
            bool ctrl = modifiers.HasFlag(VirtualKeyModifiers.Control);
            bool shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);

            if (ctrl && shift)
                return OrionBackend.OpenDisposition.NewForegroundTab;
            if (ctrl)
                return OrionBackend.OpenDisposition.NewBackgroundTab;
            if (shift)
                return OrionBackend.OpenDisposition.NewWindow;
            return null;
        }

        /// <summary>
        /// True when activating a bookmark should open a new tab instead of
        /// navigating the current one: the active tab is pinned (a bookmark never
        /// navigates a pinned tab away), or
        /// settings::browsing::open_bookmarks_in_new_tabs is on and the active tab
        /// isn't an empty new tab (which is reused instead).
        /// </summary>
        public bool ShouldOpenBookmarkInNewTab()
        {
            if (ActiveTab is not { } tab)
                return false;

            if (tab.IsPinned)
                return true;

            return OrionBackend.GetBoolSetting(_profileId,
                    "settings::browsing::open_bookmarks_in_new_tabs", "settings::browsing",
                    "open_bookmarks_in_new_tabs", false)
                && !BrowserTab.IsNewTabUrl(tab.Url);
        }

        /// <summary>
        /// Closes all tabs in the active group except the specified tab.
        /// </summary>
        public void CloseOtherTabs(BrowserTab tab)
        {
            if (ActiveGroup == null)
                return;

            bool protectPinned = OrionBackend.GetBoolSetting(_profileId,
                "settings::tabs::prevent_closing_pinned_tabs", "settings::tabs", "prevent_closing_pinned_tabs", true);

            TabGroup group = ActiveGroup.Model;
            BrowserTab[] toClose = [.. group.FlattenedTabs.Where(t => t != tab && !(protectPinned && t.IsPinned))];
            long[] dbIds = [.. toClose.Where(t => t.Id != 0 && !t.IsPinned).Select(t => t.Id)];
            foreach (BrowserTab t in toClose)
            {
                if (t.Pin?.Scope == TabPinScope.Profile)
                    _ = group.DismissedSharedPins.Add(t.Id);
                group.RemoveTab(t);
                DisposeTabViewModel(t);
            }
            if (dbIds.Length > 0)
                OrionBackend.DeleteTabs(dbIds);

            ActiveGroup.ActiveTab = tab;
        }

        /// <summary>
        /// Closes all tabs to the right of the specified tab (in flattened order).
        /// </summary>
        public void CloseTabsToTheRight(BrowserTab tab)
        {
            if (ActiveGroup == null)
                return;

            bool protectPinned = OrionBackend.GetBoolSetting(_profileId,
                "settings::tabs::prevent_closing_pinned_tabs", "settings::tabs", "prevent_closing_pinned_tabs", true);

            BrowserTab[] flat = [.. ActiveGroup.Model.FlattenedTabs];
            int index = Array.IndexOf(flat, tab);
            if (index < 0)
                return;

            BrowserTab[] toClose = [.. flat.Skip(index + 1).Where(t => !(protectPinned && t.IsPinned))];
            long[] dbIds = [.. toClose.Where(t => t.Id != 0 && !t.IsPinned).Select(t => t.Id)];
            foreach (BrowserTab t in toClose)
            {
                if (t.Pin?.Scope == TabPinScope.Profile)
                    _ = ActiveGroup.Model.DismissedSharedPins.Add(t.Id);
                ActiveGroup.RemoveTab(t, returnToLastActive: true);
                DisposeTabViewModel(t);
            }
            if (dbIds.Length > 0)
                OrionBackend.DeleteTabs(dbIds);
        }

        /// <summary>
        /// Reopens the most recently closed tab.
        /// </summary>
        public BrowserTab? ReopenClosedTab()
        {
            List<OrionBackend.RecentlyClosedTab> closed = OrionBackend.GetRecentlyClosedTabs(_profileId);
            if (closed.Count == 0)
                return null;

            OrionBackend.RecentlyClosedTab entry = closed[0];
            OrionBackend.DeleteRecentlyClosedTabs([entry.Id]);
            return CreateNewTab(entry.Uri, entry.Name);
        }

        /// <summary>
        /// Undoes the last tab close, restoring the tab at its original position.
        /// </summary>
        public BrowserTab? UndoCloseTab()
        {
            if (ActiveGroup == null)
                return null;

            TabGroup group = ActiveGroup.Model;
            (bool undoAvailable, _) = OrionBackend.IsUndoRedoTabStepAvailable(group.Id);
            if (!undoAvailable)
                return ReopenClosedTab();

            HashSet<long> existingIds = [.. group.FlattenedTabs
                .Select(t => t.Id).Where(id => id != 0)];

            OrionBackend.ChangeUndoRedoTabStep(group.Id, isUndo: true);

            List<OrionBackend.PersistedTab> dbTabs = OrionBackend.GetTabs(group.Id);
            BrowserTab? restored = null;
            foreach (OrionBackend.PersistedTab t in dbTabs)
            {
                if (existingIds.Contains(t.Id))
                    continue;

                BrowserTab tab = RestoreTab(t, group.Id);

                // Insert at correct position based on DB ordering
                int dbIndex = dbTabs.FindIndex(dt => dt.Id == t.Id);
                int insertIndex = 0;
                for (int i = 0; i < dbIndex; i++)
                {
                    BrowserTab? existing = group.FlattenedTabs
                        .FirstOrDefault(bt => bt.Id == dbTabs[i].Id);
                    if (existing != null)
                        insertIndex = group.Tabs.IndexOf(existing) + 1;
                }
                if (insertIndex >= 0 && insertIndex <= group.Tabs.Count)
                    group.Tabs.Insert(insertIndex, tab);
                else
                    group.Tabs.Add(tab);

                _ = LoadCachedFaviconAsync(tab);
                restored = tab;
            }

            group.NotifyTreeChanged();
            if (restored != null)
                ActiveGroup.ActiveTab = restored;
            return restored;
        }

        /// <summary>
        /// Redoes the last undone tab close, removing the tab again.
        /// </summary>
        public void RedoCloseTab()
        {
            if (ActiveGroup == null)
                return;

            TabGroup group = ActiveGroup.Model;
            (_, bool redoAvailable) = OrionBackend.IsUndoRedoTabStepAvailable(group.Id);
            if (!redoAvailable)
                return;

            HashSet<long> existingIds = [.. group.FlattenedTabs
                .Select(t => t.Id).Where(id => id != 0)];

            OrionBackend.ChangeUndoRedoTabStep(group.Id, isUndo: false);

            HashSet<long> dbIds = [.. OrionBackend.GetTabs(group.Id).Select(t => t.Id)];
            BrowserTab[] toRemove = [.. group.FlattenedTabs
                .Where(t => t.Id != 0 && t.Pin?.Scope != TabPinScope.Profile && !dbIds.Contains(t.Id))];

            foreach (BrowserTab tab in toRemove)
            {
                group.RemoveTab(tab);
                if (_tabViewModels.TryGetValue(tab, out BrowserTabViewModel? vm))
                {
                    vm.NativeWebView?.Dispose();
                    vm.NativeWebView = null;
                    _ = _tabViewModels.Remove(tab);
                }
            }
            group.NotifyTreeChanged();
        }

        /// <summary>
        /// Selects a tab group.
        /// </summary>
        /// <param name="group">The group to select.</param>
        public void SelectTabGroup(TabGroup group)
        {
            TabGroupViewModel? vm = FindViewModel(group);
            if (vm != null)
                ActiveGroup = vm;
        }

        /// <summary>
        /// Selects a tab group by ID.
        /// </summary>
        /// <param name="groupId">ID of the group to select.</param>
        public void SelectTabGroupById(long groupId)
        {
            TabGroupViewModel? vm = TabGroups.FirstOrDefault(vm => vm.Model.Id == groupId);
            if (vm != null)
                ActiveGroup = vm;
        }

        /// <summary>
        /// Moves a tab to a different group.
        /// </summary>
        /// <param name="tab">The tab to move.</param>
        /// <param name="targetGroup">The target group.</param>
        public void MoveTabToGroup(BrowserTab tab, TabGroup targetGroup)
        {
            if (ActiveGroup == null || tab.GroupId == targetGroup.Id || tab.Pin?.Scope == TabPinScope.Profile
                || (tab.IsPinned && (!targetGroup.IsSaved || targetGroup.IsPrivate)))
                return;

            TabGroup group = ActiveGroup.Model;
            if (tab.Pin is { Scope: TabPinScope.Workspace } pin
                && !OrionBackend.SaveTabPinState(tab.Id, targetGroup.Id, updatePin: true,
                    isPinned: true, moveToWindow: true, pin.Url, pin.Title))
            {
                return;
            }
            DetachPinFromFolder(group, tab);

            // RemoveTab handles setting a new ActiveTab
            ActiveGroup.RemoveTab(tab, returnToLastActive: true);

            if (group.Tabs.Count == 0)
                _ = CreateNewTab();

            tab.GroupId = targetGroup.Id;
            targetGroup.AddTab(tab);
            if (tab.IsPinned)
                targetGroup.PositionChangedPin(tab);

            // Persist the move
            if (tab.Id != 0 && !tab.IsPinned)
                OrionBackend.MoveTab(tab.Id, targetGroup.Id);
        }

        /// <summary>
        /// Detaches a tab group from this window without deleting it from the DB.
        /// If this was the only group, creates a new empty group first.
        /// </summary>
        /// <returns>The persisted tab group record for the detached group, or null if not found.</returns>
        public OrionBackend.PersistedTabGroup? DetachTabGroup(TabGroup group)
        {
            TabGroupViewModel? vm = FindViewModel(group);
            if (vm == null)
                return null;

            var persisted = new OrionBackend.PersistedTabGroup(
                group.Id, _profileId, group.Name,
                IsTabGroup: !group.IsSaved, SelectedTabId: vm.ActiveTab?.Id ?? 0, HasUiWindow: true,
                IsPrivate: false, KeyboardShortcut: group.KeyboardShortcut);

            // Saved groups are shared across all windows — don't remove from source.
            // Unsaved groups are moved: remove from this window.
            if (!group.IsSaved)
            {
                // If this is the last group, create a replacement before removing
                if (TabGroups.Count <= 1)
                {
                    long id = OrionBackend.CreateTabGroupEntry(_profileId,
                        isTabGroup: true, hasUiWindow: true);
                    _ = CreateTabGroupFromDb(id);
                    _ = CreateNewTab();
                }

                _ = TabGroups.Remove(vm);
                if (ActiveGroup == vm)
                    ActiveGroup = TabGroups.FirstOrDefault();
            }

            return persisted;
        }

        /// <summary>
        /// Deletes a tab group.
        /// </summary>
        /// <param name="group">The group to delete.</param>
        /// <returns>True if the group was deleted.</returns>
        public bool DeleteTabGroup(TabGroup group)
        {
            TabGroupViewModel? vm = FindViewModel(group);
            if (vm == null || !TabGroups.Remove(vm))
                return false;

            // Switch to another group if this was active
            if (ActiveGroup == vm)
                ActiveGroup = TabGroups.FirstOrDefault();

            TabGroup? shared = SharedSavedGroupsForProfile.FirstOrDefault(g => g.Id == group.Id);
            if (shared != null)
                _ = SharedSavedGroupsForProfile.Remove(shared);
            // Flip to is_tab_group=true so close_window() deletes rather than preserves
            if (group.IsSaved)
            {
                if (group.PinFolders.Count > 0)
                    _ = WritePinFolders(group, []);
                OrionBackend.UpdateTabGroupIsTabGroup(group.Id, true);
            }
            OrionBackend.DeleteTabGroup(group.Id);
            return true;
        }

        private void OnTabGroupViewModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (TabGroupViewModel vm in e.OldItems)
                {
                    vm.Model.PropertyChanged -= OnAnyGroupIsSavedChanged;
                    if (UnsavedTabGroup == vm.Model)
                        UnsavedTabGroup = null;
                }
            }
            if (e.NewItems != null)
            {
                foreach (TabGroupViewModel vm in e.NewItems)
                {
                    ProjectSharedPins(vm.Model);
                    vm.Model.PropertyChanged += OnAnyGroupIsSavedChanged;
                    if (vm.Model.IsSaved)
                    {
                        if (!SharedSavedGroupsForProfile.Contains(vm.Model))
                            SharedSavedGroupsForProfile.Add(vm.Model);
                    }
                    else
                    {
                        UnsavedTabGroup = vm.Model;
                    }
                }
            }
        }

        private void OnAnyGroupIsSavedChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabGroup.IsSaved) && sender is TabGroup group)
            {
                if (group.IsSaved)
                {
                    if (UnsavedTabGroup == group)
                        UnsavedTabGroup = null;
                    if (!SharedSavedGroupsForProfile.Contains(group))
                        SharedSavedGroupsForProfile.Add(group);
                }
                else
                {
                    UnsavedTabGroup = group;
                    // Don't remove from SharedSavedGroupsForProfile here —
                    // that would trigger other windows to drop the group.
                    // Removal happens only via DeleteTabGroup.
                }
            }
        }

        private void OnActiveGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabGroupViewModel.ActiveTab))
            {
                OnPropertyChanged(nameof(ActiveTab));
                OnPropertyChanged(nameof(ActiveTabViewModel));

                // Persist selected tab to core
                if (sender is TabGroupViewModel vm && vm.ActiveTab is { Id: not 0 } tab)
                    OrionBackend.UpdateTabGroupSelectedTab(vm.Model.Id, tab.Id);
            }

            if (e.PropertyName == nameof(TabGroupViewModel.DisplayName))
            {
                OnPropertyChanged(nameof(ActiveGroupName));
                OnPropertyChanged(nameof(SidebarHeaderText));
            }
        }

        internal string GetSearchUrlTemplate() => GetSearchEngine().UrlTemplate;

        /// <summary>The profile's default search engine (see Models/SearchEngines.cs).</summary>
        internal SearchEngineInfo GetSearchEngine()
        {
            var engine = (SearchEngine)OrionBackend.GetIntSetting(ActiveProfileId,
                "settings::search::search_engine", "settings::search", "search_engine", (int)SearchEngine.kagi);
            return SearchEngines.Get(engine);
        }

        public static string NormalizeUrl(string input, string searchUrlTemplate = SearchEngines.DefaultUrlTemplate)
        {
            return Uri.TryCreate(input, UriKind.Absolute, out _) ? input
                : input.Contains('.') && !input.Contains(' ') ? "https://" + input
                : searchUrlTemplate.Replace("%s", Uri.EscapeDataString(input));
        }

        private static async Task LoadCachedFaviconAsync(BrowserTab tab)
        {
            if (string.IsNullOrEmpty(tab.Url) || tab.Url == "about:blank")
                return;
            OrionBackend.FaviconResult? result = OrionBackend.GetFavicon(tab.Url);
            if (result != null)
                tab.FaviconImage = await BrowserTab.CreateFaviconImageAsync(result.Data, result.IconUrl);
        }

        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/60.5 Safari/605.1.15";

        public static string? GetUserAgentString(long profileId)
        {
            int value = OrionBackend.GetIntSetting(profileId,
                "settings::websites::user_agent_setting_for_all_websites",
                "settings::websites", "user_agent_setting_for_all_websites", 1);
            return value switch
            {
                2 => OrionBackend.GetStringSetting(profileId,
                    "settings::privacy::custom_user_agent",
                    "settings::privacy", "custom_user_agent", ""),
                3 => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36",
                4 => "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:150.0) Gecko/20100101 Firefox/150.0",
                5 => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
                6 => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1",
                7 => "Mozilla/5.0 (iPad; CPU OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1",
                8 => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0",
                _ => DefaultUserAgent,
            };
        }

        public void ApplyUserAgentToAllViews()
        {
            string? ua = GetUserAgentString(ActiveProfileId);
            int count = 0;
            foreach (BrowserTabViewModel vm in _tabViewModels.Values)
            {
                if (vm.NativeWebView != null) count++;
                vm.NativeWebView?.SetUserAgent(ua);
            }
            OrionBackend.LogDebug($"ApplyUserAgentToAllViews: ua={(ua != null ? $"'{(ua.Length > 30 ? $"{ua[..30]}..." : ua)}'" : "null")}, views={count}/{_tabViewModels.Count}");
        }

        internal void RestoreFromSnapshot()
        {
            DestroyAllWebViews();

            // Clear UI state without touching the DB (restore already replaced it)
            SharedSavedGroupsForProfile.CollectionChanged -= OnSharedSavedGroupsChanged;
            TabGroups.Clear();
            ActiveGroup = null;
            UnsavedTabGroup = null;
            _sharedSavedGroupsByProfile.Clear();
            _sharedPins.Reload(resolveWindow: true);

            var groups = OrionBackend.GetTabGroups(_profileId)
                .Where(g => g.HasUiWindow).ToList();
            RestoreTabGroups(groups);

            LoadSavedTabGroups();
            SharedSavedGroupsForProfile.CollectionChanged += OnSharedSavedGroupsChanged;
        }

        internal void DestroyAllWebViews()
        {
            foreach (BrowserTabViewModel vm in _tabViewModels.Values)
            {
                vm.NativeWebView?.Dispose();
                vm.NativeWebView = null;
            }
            _tabViewModels.Clear();
        }
    }
}
