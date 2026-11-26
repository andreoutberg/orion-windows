using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private readonly Dictionary<SidebarPage, RadioMenuFlyoutItem> _sidebarContextMenuItems = [];
        private readonly Dictionary<SidebarPage, Button> _sidebarNavButtons = [];

        private void BuildSidebarPageUI()
        {
            var activeBrush = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
            var transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            foreach (SidebarPage page in Enum.GetValues<SidebarPage>()[1..])
            {
                // Context menu radio item
                var menuItem = new RadioMenuFlyoutItem { Text = page.DisplayName(), GroupName = "SidebarContent" };
                menuItem.Click += (s, e) => SetSidebarPage(page);
                SidebarContextMenu.Items.Add(menuItem);
                _sidebarContextMenuItems[page] = menuItem;

                // Footer nav button
                var button = new Button
                {
                    Width = 36,
                    Height = 34,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Style = (Style)Application.Current.Resources["OrionChromeButtonStyle"],
                    Background = transparentBrush,
                    Content = new FontIcon { FontSize = 15, Glyph = page.Glyph() },
                };
                AutomationProperties.SetName(button, page.DisplayName());
                ToolTipService.SetToolTip(button, page.DisplayName());
                button.Click += (s, e) => SetSidebarPage(page);
                SidebarNavButtons.Children.Add(button);
                _sidebarNavButtons[page] = button;
            }

            SidebarContextMenu.Items.Add(new MenuFlyoutSeparator());
            SidebarContextMenu.Items.Add(new MenuFlyoutItem { Text = "Sidebar Settings" });
            ((MenuFlyoutItem)SidebarContextMenu.Items[^1]).Click += SidebarSettings_Click;

            // Subscribe to ViewModel changes for button highlighting
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.SelectedSidebarPage))
                    UpdateSidebarNavHighlight(activeBrush, transparentBrush);
                if (e.PropertyName == nameof(MainWindowViewModel.TabLayout))
                    UpdateTabLayoutMenu();
                if (e.PropertyName is nameof(MainWindowViewModel.TabLayout)
                    or nameof(MainWindowViewModel.ShowSidebar)
                    or nameof(MainWindowViewModel.IsChromeHiddenForFullscreen))
                {
                    UpdateSidebarPresentation();
                }
            };
            UpdateSidebarNavHighlight(activeBrush, transparentBrush);
            UpdateTabLayoutMenu();
            UpdateSidebarPresentation();
        }

        private void UpdateSidebarNavHighlight(Brush? activeBrush, Brush transparentBrush)
        {
            SidebarPage selected = ViewModel.SelectedSidebarPage;
            SidebarPageLabel.Text = selected.DisplayName();
            foreach ((SidebarPage page, Button button) in _sidebarNavButtons)
            {
                button.Background = page == selected ? activeBrush : transparentBrush;
                AutomationProperties.SetItemStatus(button, page == selected ? "Selected" : string.Empty);
            }
        }

        /// <summary>
        /// Central method to switch sidebar page, keeping context menu radio items in sync.
        /// </summary>
        private void SetSidebarPage(SidebarPage page)
        {
            ViewModel.SelectedSidebarPage = page;
            foreach ((SidebarPage m, RadioMenuFlyoutItem item) in _sidebarContextMenuItems)
                item.IsChecked = m == page;
            if (page == SidebarPage.bookmarks)
                InitializeBookmarks();
            if (page == SidebarPage.reading_list)
                InitializeReadingList();
        }

        private void TabLayoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, StandardLayoutMenuItem))
            {
                ViewModel.TabLayout = TabLayout.standard;
            }
            else if (ReferenceEquals(sender, CompactLayoutMenuItem))
            {
                ViewModel.TabLayout = TabLayout.compact;
            }
            else if (ReferenceEquals(sender, VerticalLayoutMenuItem))
            {
                ViewModel.TabLayout = TabLayout.vertical;
                SetSidebarPage(SidebarPage.tabs);
            }
        }

        private void UpdateTabLayoutMenu()
        {
            StandardLayoutMenuItem.IsChecked = ViewModel.TabLayout == TabLayout.standard;
            CompactLayoutMenuItem.IsChecked = ViewModel.TabLayout == TabLayout.compact;
            VerticalLayoutMenuItem.IsChecked = ViewModel.TabLayout == TabLayout.vertical;
        }

        private void SidebarSettings_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show sidebar settings
        }


        private void BookmarksTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
        {
            if (e.InvokedItem is BookmarkTreeNode { IsFolder: false } node && !string.IsNullOrEmpty(node.Url))
                OpenBookmark(node.Url, node.Title);
        }

        private void BookmarkTreeItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed
                && sender is TreeViewItem { DataContext: BookmarkTreeNode { IsFolder: false } node }
                && !string.IsNullOrEmpty(node.Url))
            {
                OpenLink(node.Url, OrionBackend.OpenDisposition.NewBackgroundTab, node.Title);
                e.Handled = true;
            }
        }

        private void BookmarksTreeView_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs e)
        {
            foreach (object obj in e.Items)
            {
                if (obj is not BookmarkTreeNode moved)
                    continue;

                (IList<BookmarkTreeNode> Siblings, int Index, long ParentFolderId)? location = BookmarkTreeNode.FindInTree(_bookmarkNodes, moved);
                if (location is not null)
                {
                    (IList<BookmarkTreeNode>? siblings, int idx, long parentFolderId) = location.Value;
                    BookmarkTreeNode.PersistMove(moved, siblings, idx, parentFolderId);
                }
            }
            InitializeBookmarkBar();
        }

        private void BookmarksAddFolder_Click(object sender, RoutedEventArgs e) => _ = ShowAddBookmarkFolderDialog();

        private void BookmarkItem_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: BookmarkTreeNode node })
            {
                if (node.IsFolder)
                    OrionBackend.DeleteBookmarkFolder(node.EntryId);
                else
                    OrionBackend.DeleteBookmark(node.EntryId);
                BookmarksChanged.Notify();
            }
        }

        private async System.Threading.Tasks.Task ShowAddBookmarkFolderDialog()
        {
            var nameBox = new TextBox { PlaceholderText = "Folder name" };

            var dialog = new PopupDialog
            {
                Title = "New Folder",
                DialogContent = nameBox,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
            };

            PopupDialogResult result = await dialog.ShowAsync(this);

            if (result == PopupDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
            {
                _ = OrionBackend.CreateBookmarkFolder(ViewModel.ActiveProfileId, nameBox.Text);
                BookmarksChanged.Notify();
            }
        }

        private void ReadingListItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ReadingListEntry entry)
            {
                if (entry.IsUnread)
                {
                    entry.IsUnread = false;
                    OrionBackend.MarkReadingListEntryRead(entry.Id.Value, false);
                    ReadingListChanged.Notify();
                }
                NavigateToUrl(entry.Address);
            }
        }

        private void NewWindow_Click(object sender, RoutedEventArgs e) => _ = MainWindow.OpenForProfile(ViewModel.ActiveProfile);

        private void History_Click(object sender, RoutedEventArgs e) => LibraryWindow.ShowHistory(profileId: ViewModel.ActiveProfileId);

        private void DownloadsButton_Click(object sender, RoutedEventArgs e) => OpenDownloadsTab();

        private readonly Dictionary<long, (long received, long total)> _activeDownloads = [];

        private void InitializeDownloadTracking()
        {
            OrionBackend.DownloadCreated += OnToolbarDownloadChanged;
            OrionBackend.DownloadUpdated += OnToolbarDownloadChanged;
            OrionBackend.DownloadDeleted += OnToolbarDownloadDeleted;
        }

        private void CleanupDownloadTracking()
        {
            OrionBackend.DownloadCreated -= OnToolbarDownloadChanged;
            OrionBackend.DownloadUpdated -= OnToolbarDownloadChanged;
            OrionBackend.DownloadDeleted -= OnToolbarDownloadDeleted;
        }

        private void OnToolbarDownloadChanged(DownloadEntry entry)
        {
            if (entry.ProfileId != ViewModel.ActiveProfileId)
                return;
            if (entry.Status == DownloadStatus.Downloading)
                _activeDownloads[entry.Id] = (entry.ReceivedBytes, entry.TotalBytes);
            else
                _activeDownloads.Remove(entry.Id);
            UpdateDownloadProgressOverlay();
        }

        private void OnToolbarDownloadDeleted(long downloadId)
        {
            _activeDownloads.Remove(downloadId);
            UpdateDownloadProgressOverlay();
        }

        private void UpdateDownloadProgressOverlay()
        {
            bool hasDownloads = _activeDownloads.Count > 0;
            DownloadsButton.Visibility = hasDownloads ? Visibility.Visible : Visibility.Collapsed;
            if (!hasDownloads)
                return;

            long totalReceived = _activeDownloads.Values.Sum(d => d.received);
            long totalSize = _activeDownloads.Values.Sum(d => d.total);
            bool hasProgress = totalSize > 0 && totalReceived <= totalSize;
            string progress = hasProgress ? $"{(double)totalReceived / totalSize:P0}" : string.Empty;
            string label = hasProgress ? $"Downloads in progress, {progress}" : "Downloads in progress";
            DownloadProgressText.Text = progress;
            DownloadProgressText.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
            DownloadsIcon.Visibility = hasProgress ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(DownloadsButton, label);
            ToolTipService.SetToolTip(DownloadsButton, label);
        }

        private void ExtensionsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsTab("Extensions");

        internal void RefreshExtensionToolbarItems()
        {
            ExtensionToolbarItems.Children.Clear();
            List<ExtensionToolbarItem> actions = OrionBackend.GetExtensionToolbarItems(ViewModel.ActiveProfileId);
            foreach (ExtensionToolbarItem action in actions)
            {
                var button = new Button
                {
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(0, 0, 4, 0),
                };
                ToolTipService.SetToolTip(button, action.Title);

                if (action.IconData != null)
                {
                    var img = new Image { Width = 16, Height = 16 };
                    var bmp = new BitmapImage();
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        stream.WriteAsync(action.IconData.AsBuffer()).GetResults();
                        stream.Seek(0);
                        bmp.SetSource(stream);
                    }
                    img.Source = bmp;
                    button.Content = img;
                }
                else
                {
                    string initial = action.Title.Length > 0 ? action.Title[..1] : "?";
                    button.Content = new TextBlock
                    {
                        Text = initial,
                        FontSize = 14,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }

                string popupUri = action.PopupUri;
                button.Click += (_, _) => _ = ViewModel.CreateNewTab(popupUri);
                ExtensionToolbarItems.Children.Add(button);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsTab();

        private void About_Click(object sender, RoutedEventArgs e) => OpenSettingsTab("About");
    }
}
