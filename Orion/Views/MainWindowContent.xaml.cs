using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Orion
{
    /// <summary>
    /// Main browser UI content. Extracted from MainWindow to enable x:Bind
    /// (UserControl is a FrameworkElement; Window is not).
    /// </summary>
    public sealed partial class MainWindowContent : UserControl
    {
        private readonly ObservableCollection<BookmarkTreeNode> _bookmarkNodes = [];
        private readonly ObservableCollection<ReadingListEntry> _readingList = [];

        /// <summary>
        /// ViewModel for data binding.
        /// </summary>
        public MainWindowViewModel ViewModel { get; }

        /// <summary>
        /// Reference to the parent Window for native interop (e.g., getting HWND).
        /// Also sets the HwndHostPanel's window handle so child HWNDs are parented correctly.
        /// </summary>
        public Window? ParentWindow
        {
            get;
            set
            {
                field = value;
                if (value != null)
                {
                    Win32.HWND hwnd = WinRT.Interop.WindowNative.GetWindowHandle(value);
                    WebViewHost.WindowHandle = hwnd;
                    DevToolsPane.WindowHandle = hwnd;
                }
            }
        }

        /// <summary>
        /// Exposes the TitleBar element for Window.SetTitleBar().
        /// </summary>
        internal UIElement TitleBarElement => AppTitleBar;

        public MainWindowContent()
        {
            // Resolve profile: MainWindow.PendingProfile is set before
            // InitializeComponent; fall back to the profile this process
            // hosts (one profile per process).
            Profile profile = MainWindow.PendingProfile
                ?? OrionBackend.GetProfiles().FirstOrDefault(p => p.Id == OrionBackend.GetCurrentProfileId())
                ?? OrionBackend.GetProfiles().First(p => p.IsDefault);

            // ViewModel must be created before InitializeComponent so x:Bind can resolve
            ViewModel = new MainWindowViewModel(profile, MainWindow.PendingTabGroups);

            // Load sidebar settings before InitializeComponent so IsPaneOpen binding
            // picks up the correct value immediately (avoids visual flicker)
            long initPid = ViewModel.ActiveProfileId;
            bool previewFirstRun = System.Environment.GetEnvironmentVariable("ORION_DESIGN_PREVIEW") == "1"
                && System.Environment.GetEnvironmentVariable("ORION_DESIGN_FIRST_RUN") == "1";
            if (previewFirstRun)
            {
                System.Environment.SetEnvironmentVariable("ORION_DESIGN_FIRST_RUN", null);
            }
            ViewModel.ShowSidebar = previewFirstRun || OrionBackend.GetBoolSetting(initPid,
                "settings::appearance::show_sidebar", "settings::appearance", "show_sidebar", false);
            ViewModel.SelectedSidebarPage = previewFirstRun ? SidebarPage.tabs : (SidebarPage)OrionBackend.GetIntSetting(initPid,
                "settings::appearance::sidebar_page", "settings::appearance", "sidebar_page",
                (int)SidebarPage.windows);

            InitializeComponent();
            InitializeTabTracking();
            InitializeTabGroupContextMenus();
            RegisterKeyboardAccelerators();
            InitializeFullscreen();
            InitializeTabHoverPreview();
            InitializeHistoryRecording();
            InitializeFindBar();
            InitializeDevToolsPane();
            ThemeService.Register(this);

            // Build the hamburger menu and sidebar mode UI
            BuildMainMenu();
            BuildSidebarPageUI();
            SetSidebarPage(ViewModel.SelectedSidebarPage);

            // Wire up settings page
            SettingsPageContent.MainViewModel = ViewModel;

            // Wire up downloads page
            DownloadsPageContent.ProfileId = ViewModel.ActiveProfileId;

            // Wire up start page
            StartPageContent.NavigateRequested += (_, url) => NavigateToUrl(url);
            StartPageContent.AddressRequested += (_, _) => _ = FocusAddressBar();

            // Initialize profile-dependent settings
            long pid = ViewModel.ActiveProfileId;
            ViewModel.ShowBookmarkBar = !previewFirstRun && OrionBackend.GetBoolSetting(pid,
                "settings::appearance::show_bookmarks_bar", "settings::appearance", "show_bookmarks_bar", true);
            if (previewFirstRun)
            {
                OrionBackend.SetBoolSetting(pid, "settings::appearance::show_bookmarks_bar",
                    "settings::appearance", "show_bookmarks_bar", false, true);
            }
            ViewModel.TabLayout = previewFirstRun ? TabLayout.vertical : (TabLayout)OrionBackend.GetIntSetting(pid,
                "settings::tabs::tab_layout", "settings::tabs", "tab_layout", (int)TabLayout.standard);
            ViewModel.ProfileDisplayMode = (ProfileDisplayMode)OrionBackend.GetIntSetting(pid,
                "settings::appearance::profile_display_mode", "settings::appearance", "profile_display_mode",
                (int)ProfileDisplayMode.IconOnly);
            ViewModel.ShowNestedVerticalTabs = OrionBackend.GetBoolSetting(pid,
                "settings::tabs::show_nested_vertical_tabs", "settings::tabs", "show_nested_vertical_tabs", true);
            ViewModel.ShowFaviconsInTabs = OrionBackend.GetBoolSetting(pid,
                "settings::tabs::show_favicons_inside_tabs", "settings::tabs", "show_favicons_inside_tabs", true);
            ViewModel.ShowWebsiteTitlesInTabs = OrionBackend.GetBoolSetting(pid,
                "settings::tabs::always_show_website_titles_in_tabs", "settings::tabs", "always_show_website_titles_in_tabs", true);

            // Set up scroll viewer height constraint
            SidebarSplitView.SizeChanged += SplitView_SizeChanged;
            SidebarSplitView.Loaded += (s, e) => UpdateVerticalTabScrollViewerHeight();

            // Initialize WebKit view when content is loaded
            WebViewHost.Loaded += OnWebViewHostLoaded;

            // Reading list (loaded lazily) and bookmarks (needed for bookmark bar)
            ReadingListView.ItemsSource = _readingList;
            ReadingListChanged.Changed += InitializeReadingList;
            BookmarksChanged.Changed += OnBookmarksChanged;
            InitializeBookmarkBar();

            RefreshExtensionToolbarItems();
            ExtensionsChanged.Changed += RefreshExtensionToolbarItems;

            InitializeDownloadTracking();
        }

        private void OnBookmarksChanged()
        {
            InitializeBookmarkBar();
            if (ViewModel.SelectedSidebarPage == SidebarPage.bookmarks)
                InitializeBookmarks();
        }

        private void InitializeReadingList()
        {
            _readingList.Clear();
            foreach (ReadingListEntry entry in OrionBackend.GetReadingList(ViewModel.ActiveProfileId))
            {
                _readingList.Add(entry);
                _ = entry.LoadFaviconAsync();
            }
        }

        private void InitializeBookmarks()
        {
            _bookmarkNodes.Clear();
            try
            {
                var allEntries = new List<BookmarkEntry>(OrionBackend.GetBookmarkFolders(ViewModel.ActiveProfileId));
                allEntries.AddRange(OrionBackend.GetBookmarks(ViewModel.ActiveProfileId));
                BookmarkTreeNode.BuildTree(_bookmarkNodes, allEntries);
                LoadBookmarkFavicons(_bookmarkNodes);
            }
            catch
            {
                // Fall back to empty tree if core module not available
            }
        }

        private static void LoadBookmarkFavicons(IEnumerable<BookmarkTreeNode> nodes)
        {
            foreach (BookmarkTreeNode node in nodes)
            {
                _ = node.LoadFaviconAsync();
                if (node.IsFolder)
                    LoadBookmarkFavicons(node.Children);
            }
        }

        /// <summary>Bookmarks Bar folder contents in display order (bar buttons, Ctrl+1..9).</summary>
        private List<BookmarkTreeNode> GetBookmarkBarNodes()
        {
            var allEntries = new List<BookmarkEntry>(OrionBackend.GetBookmarkFolders(ViewModel.ActiveProfileId));
            allEntries.AddRange(OrionBackend.GetBookmarks(ViewModel.ActiveProfileId));
            var barNodes = new List<BookmarkTreeNode>();
            BookmarkTreeNode.BuildTree(barNodes, allEntries);

            // Find the Bookmarks Bar folder (FolderType == 1) and use its children
            BookmarkTreeNode? barFolder = barNodes.FirstOrDefault(n => n.IsFolder && n.Entry.FolderType == 1);
            IEnumerable<BookmarkTreeNode> items = barFolder != null
                ? barFolder.Children
                : barNodes.Where(n => !n.IsFolder);
            return [.. items];
        }

        private void InitializeBookmarkBar()
        {
            BookmarkBarPanel.Children.Clear();
            try
            {
                foreach (BookmarkTreeNode node in GetBookmarkBarNodes())
                    BookmarkBarPanel.Children.Add(CreateBookmarkBarButton(node));
            }
            catch
            {
                // Fall back to empty bar if core module not available
            }
        }

        private Button CreateBookmarkBarButton(BookmarkTreeNode node)
        {
            var btn = new Button
            {
                Padding = new Thickness(8, 4, 8, 4),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Tag = node,
            };

            if (node.IsFolder)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                panel.Children.Add(new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), Glyph = "\uE8B7", FontSize = 14 });
                panel.Children.Add(new TextBlock { Text = node.Title });
                panel.Children.Add(new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), Glyph = "\uE70D", FontSize = 8 });
                btn.Content = panel;

                var flyout = new MenuFlyout();
                PopulateBookmarkBarFolderFlyout(flyout, node);
                btn.Flyout = flyout;
            }
            else
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var img = new Image { Width = 14, Height = 14, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
                panel.Children.Add(img);
                panel.Children.Add(new TextBlock { Text = node.Title });
                btn.Content = panel;
                btn.Click += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(node.Url))
                        OpenBookmark(node.Url, node.Title);
                };
                btn.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed
                        && !string.IsNullOrEmpty(node.Url))
                    {
                        OpenLink(node.Url, OrionBackend.OpenDisposition.NewBackgroundTab, node.Title);
                        e.Handled = true;
                    }
                };
                _ = LoadBookmarkBarFaviconAsync(node, img);
            }

            return btn;
        }

        private void PopulateBookmarkBarFolderFlyout(MenuFlyout flyout, BookmarkTreeNode folder)
        {
            if (folder.Children.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem { Text = "(Empty)", IsEnabled = false });
                return;
            }
            foreach (BookmarkTreeNode child in folder.Children)
            {
                if (child.IsFolder)
                {
                    var sub = new MenuFlyoutSubItem { Text = child.Title };
                    PopulateBookmarkBarSubFolder(sub, child);
                    flyout.Items.Add(sub);
                }
                else
                {
                    var item = new MenuFlyoutItem { Text = child.Title };
                    string url = child.Url;
                    string title = child.Title;
                    item.Click += (s, e) => OpenBookmark(url, title);
                    flyout.Items.Add(item);
                    _ = LoadMenuFlyoutFaviconAsync(child, item);
                }
            }
        }

        private void PopulateBookmarkBarSubFolder(MenuFlyoutSubItem parent, BookmarkTreeNode folder)
        {
            if (folder.Children.Count == 0)
            {
                parent.Items.Add(new MenuFlyoutItem { Text = "(Empty)", IsEnabled = false });
                return;
            }
            foreach (BookmarkTreeNode child in folder.Children)
            {
                if (child.IsFolder)
                {
                    var sub = new MenuFlyoutSubItem { Text = child.Title };
                    PopulateBookmarkBarSubFolder(sub, child);
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

        private static async System.Threading.Tasks.Task LoadBookmarkBarFaviconAsync(BookmarkTreeNode node, Image img)
        {
            await node.LoadFaviconAsync();
            if (node.FaviconImage != null)
                img.Source = node.FaviconImage;
        }

        private static async System.Threading.Tasks.Task LoadMenuFlyoutFaviconAsync(BookmarkTreeNode node, MenuFlyoutItem item)
        {
            await node.LoadFaviconAsync();
            if (node.FaviconImage != null)
                item.Icon = new ImageIcon { Source = node.FaviconImage };
        }

        private static async System.Threading.Tasks.Task LoadMenuFlyoutFaviconAsync(string url, MenuFlyoutItem item)
        {
            OrionBackend.FaviconResult? result = OrionBackend.GetFavicon(url);
            if (result != null)
            {
                Microsoft.UI.Xaml.Media.ImageSource? src = await BrowserTab.CreateFaviconImageAsync(result.Data, result.IconUrl);
                if (src != null)
                    item.Icon = new ImageIcon { Source = src };
            }
        }

        /// <summary>
        /// Clean up native resources.
        /// </summary>
        public void Cleanup()
        {
            CloseWebContextMenu();
            StopHoverTimer();
            HideFullscreenToast();
            UnblockDisplaySleep();
            UnregisterFullscreenSettingObservers();
            UnregisterTabHoverPreview();
            UnregisterHistoryRecording();
            ReadingListChanged.Changed -= InitializeReadingList;
            BookmarksChanged.Changed -= OnBookmarksChanged;
            EntityShortcuts.Changed -= RebuildEntityAccelerators;
            CleanupDownloadTracking();

            WebViewHost.HideAll();
            DevToolsPane.Hide();
            ViewModel.DestroyAllWebViews();
        }

    }
}
