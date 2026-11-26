using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        /// <summary>
        /// Hides all internal orion:// page controls.
        /// </summary>
        private void HideAllInternalPages()
        {
            SettingsPageContent.Visibility = Visibility.Collapsed;
            DownloadsPageContent.Visibility = Visibility.Collapsed;
            StartPageContent.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Shows the active tab's content without re-navigating. Used on tab switch
        /// to avoid redundant LoadUrl calls that trigger favicon re-downloads.
        /// </summary>
        private void ShowActiveTab(BrowserTab tab)
        {
            string url = tab.Url;
            AddressBarTextBox.Text = BrowserTab.AddressBarTextForUrl(url);

            if (url.StartsWith("orion://"))
            {
                WebViewHost.HideAll();
                WebViewHost.Visibility = Visibility.Collapsed;
                HideAllInternalPages();

                if (url.StartsWith("orion://settings"))
                    SettingsPageContent.Visibility = Visibility.Visible;
                else if (url.StartsWith("orion://downloads"))
                    DownloadsPageContent.Visibility = Visibility.Visible;
                else if (BrowserTab.IsStartPageUrl(url))
                    StartPageContent.Visibility = Visibility.Visible;
            }
            else
            {
                HideAllInternalPages();
                WebViewHost.Visibility = Visibility.Visible;
                NativeWebView? webView = ViewModel.GetOrCreateTabViewModel(tab).NativeWebView;
                if (webView != null)
                    _ = WebViewHost.Show(webView.Hwnd);
            }

        }

        /// <summary>
        /// Navigates to the specified URL
        /// </summary>
        internal void NavigateToUrl(string url)
        {
            AddressBarTextBox.Text = BrowserTab.AddressBarTextForUrl(url);

            BrowserTab? activeTab = ViewModel.ActiveTab;

            // Update the active tab's URL
            activeTab?.Url = url;

            // Show the appropriate content based on URL scheme
            if (url.StartsWith("orion://"))
            {
                WebViewHost.HideAll();
                WebViewHost.Visibility = Visibility.Collapsed;
                HideAllInternalPages();

                if (url.StartsWith("orion://settings"))
                {
                    SettingsPageContent.Visibility = Visibility.Visible;
                }
                else if (url.StartsWith("orion://downloads"))
                {
                    DownloadsPageContent.Visibility = Visibility.Visible;
                }
                else if (BrowserTab.IsStartPageUrl(url))
                {
                    StartPageContent.Visibility = Visibility.Visible;
                }
            }
            else
            {
                HideAllInternalPages();

                if (activeTab != null)
                    EnsureWebView(activeTab);

                if (ViewModel.ActiveTab is { IsCrashed: true })
                    ViewModel.ActiveTab.IsCrashed = false;

                WebViewHost.Visibility = Visibility.Visible;
                NativeWebView? webView = ViewModel.ActiveTabViewModel?.NativeWebView;
                if (webView != null)
                {
                    _ = WebViewHost.Show(webView.Hwnd);
                    webView.LoadUrl(url);
                }
            }

            if (activeTab != null)
                SyncDevToolsPaneToActiveTab(activeTab);
        }

        /// <summary>
        /// Opens an activated bookmark. A modifier gesture (Ctrl/Shift held during
        /// activation) diverts it like a modifier-clicked link; otherwise it opens
        /// in a new tab when the active tab is pinned or
        /// settings::browsing::open_bookmarks_in_new_tabs directs it there, else in
        /// the current tab. Keyboard bookmark shortcuts (Ctrl+1..9) bypass this and
        /// always navigate in place.
        /// </summary>
        internal void OpenBookmark(string url, string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                title = null;

            if (MainWindowViewModel.BookmarkGestureDisposition(GetCurrentCtrlShiftModifiers())
                is { } disposition)
            {
                OpenLink(url, disposition, title);
                return;
            }

            if (ViewModel.ShouldOpenBookmarkInNewTab())
                _ = ViewModel.CreateNewTab(url, title);
            else
                NavigateCurrentTabToBookmark(url, title);
        }

        /// <summary>
        /// Navigates the current tab to a bookmark, showing the bookmark's title
        /// while the page loads (the engine's titleChanged replaces it).
        /// </summary>
        internal void NavigateCurrentTabToBookmark(string url, string? title)
        {
            if (!string.IsNullOrWhiteSpace(title) && ViewModel.ActiveTab is { } tab)
                tab.Title = title;
            NavigateToUrl(url);
        }

        private static Windows.System.VirtualKeyModifiers GetCurrentCtrlShiftModifiers() =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control).HasFlag(
                Windows.UI.Core.CoreVirtualKeyStates.Down) ? Windows.System.VirtualKeyModifiers.Control : 0) |
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Shift).HasFlag(
                Windows.UI.Core.CoreVirtualKeyStates.Down) ? Windows.System.VirtualKeyModifiers.Shift : 0);

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
            System.Collections.Generic.List<Profile> profiles = OrionBackend.GetProfiles();

            // All profiles — each switches to that profile: focuses its
            // existing window (or process), opening one only if none exists.
            // Matches Mac; a new window is Ctrl+N / File > New Window.
            foreach (Profile profile in profiles)
            {
                MenuFlyoutItem item = CreateProfileMenuItem(profile);
                Profile capturedProfile = profile;
                item.Click += (_, _) => MainWindow.FocusOrOpenForProfile(capturedProfile);
                flyout.Items.Add(item);
            }

            // Actions
            flyout.Items.Add(new MenuFlyoutSeparator());
            var newProfileItem = new MenuFlyoutItem { Text = "New Profile" };
            newProfileItem.Click += (_, _) =>
            {
                long newId = SettingsPage.CreateProfileLike(ViewModel.ActiveProfileId);
                OpenSettingsTab("Profiles");
                if (newId != 0)
                    SettingsPageContent.SelectProfile(newId);
            };
            flyout.Items.Add(newProfileItem);

            var manageItem = new MenuFlyoutItem { Text = "Manage Profiles" };
            manageItem.Click += (_, _) => OpenSettingsTab("Profiles");
            flyout.Items.Add(manageItem);

            // Display mode options
            flyout.Items.Add(new MenuFlyoutSeparator());

            var iconOnly = new RadioMenuFlyoutItem
            {
                Text = "Show Icon Only",
                GroupName = "ProfileDisplay",
                IsChecked = ViewModel.ProfileDisplayMode == ProfileDisplayMode.IconOnly,
            };
            iconOnly.Click += (_, _) => ViewModel.ProfileDisplayMode = ProfileDisplayMode.IconOnly;
            flyout.Items.Add(iconOnly);

            var nameOnly = new RadioMenuFlyoutItem
            {
                Text = "Show Name Only",
                GroupName = "ProfileDisplay",
                IsChecked = ViewModel.ProfileDisplayMode == ProfileDisplayMode.NameOnly,
            };
            nameOnly.Click += (_, _) => ViewModel.ProfileDisplayMode = ProfileDisplayMode.NameOnly;
            flyout.Items.Add(nameOnly);

            var iconAndName = new RadioMenuFlyoutItem
            {
                Text = "Show Icon and Name",
                GroupName = "ProfileDisplay",
                IsChecked = ViewModel.ProfileDisplayMode == ProfileDisplayMode.IconAndName,
            };
            iconAndName.Click += (_, _) => ViewModel.ProfileDisplayMode = ProfileDisplayMode.IconAndName;
            flyout.Items.Add(iconAndName);

            flyout.ShowAt(ProfileButton);
        }

        private static MenuFlyoutItem CreateProfileMenuItem(Profile profile)
        {
            var icon = new FontIcon
            {
                Glyph = profile.DisplayIcon,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Emoji"),
                FontSize = 14,
                Foreground = profile.IconForeground,
            };
            var item = new MenuFlyoutItem
            {
                Text = profile.Name,
                Icon = icon,
                Tag = profile,
                KeyboardAcceleratorTextOverride =
                    KeyCombo.TryParseGtkAccel(profile.KeyboardShortcut, out KeyCombo shortcut)
                        ? shortcut.ToDisplayString() : string.Empty,
            };
            return item;
        }

        private void WebsiteInfo_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show site information
        }

        private void WebsitePermissions_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show website permissions
        }

        private void WebsiteCookies_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show website cookies
        }

        private void PageZoom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && item.Tag is string zoomLevel)
            {
                _ = int.Parse(zoomLevel);
                // TODO: Apply zoom level to current page
            }
        }

        private void AddressBarTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;

            // Ignore Enter on an empty address bar (e.g. the start page) instead of
            // running an empty search.
            if (string.IsNullOrWhiteSpace(AddressBarTextBox.Text))
                return;

            string searchTemplate = ViewModel.GetSearchUrlTemplate();
            string url = MainWindowViewModel.NormalizeUrl(AddressBarTextBox.Text, searchTemplate);
            NavigateToUrl(url);
        }

        /// <summary>
        /// TextBox's internal OnKeyDown sends WM_KEYDOWN to RichEdit, which handles
        /// VK_TAB by inserting a tab character and marking the event as handled — our
        /// KeyDown handler never fires. PreviewKeyDown runs before that, so we can
        /// intercept Ctrl+Tab and dispatch it as a shortcut instead.
        /// </summary>
        private void AddressBarTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Tab)
                return;

            if (DispatchShortcut(e.Key, GetCurrentCtrlShiftModifiers()))
                e.Handled = true;
        }

        private bool FocusAddressBar() => AddressBarTextBox.Focus(FocusState.Keyboard);

        private void AddressBarTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            AddressBarTextBox.SelectAll();
            AnimateChromeOpacity(AddressFocusRing, 0, 1, 150);
        }

        private void AddressBarTextBox_LostFocus(object sender, RoutedEventArgs e) =>
            AnimateChromeOpacity(AddressFocusRing, 1, 0, 120);

        // Shift or Ctrl held at click bypasses the cache, like Chrome's reload button.
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveTabViewModel is not { } tabVm)
                return;
            if (GetCurrentCtrlShiftModifiers() != Windows.System.VirtualKeyModifiers.None)
                tabVm.ReloadBypassingCacheCommand.Execute(null);
            else
                tabVm.ReloadCommand.Execute(null);
        }

        private void BookmarkButton_Click(object sender, RoutedEventArgs e) => _ = ShowAddBookmarkDialog();


        private async System.Threading.Tasks.Task ShowAddBookmarkDialog(BrowserTab? tab = null)
        {
            if (_bookmarkNodes.Count == 0)
                InitializeBookmarks();

            tab ??= ViewModel.ActiveTab;
            string defaultName = tab?.Title ?? "";
            string defaultUrl = tab?.Url ?? "";

            var locationCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            locationCombo.Items.Add(new ComboBoxItem { Content = "\uE736  Reading List", Tag = "reading_list" });
            int defaultIndex = 1; // default to first bookmark folder (index 0 is Reading List)
            int comboIdx = 1; // offset by 1 for the reading list item
            AddFolderNodes(locationCombo, _bookmarkNodes, 0, ref defaultIndex, ref comboIdx);
            if (locationCombo.Items.Count == 1) // only reading list item
                locationCombo.Items.Add(new ComboBoxItem { Content = "Root", Tag = 0L });
            locationCombo.SelectedIndex = defaultIndex;

            var nameBox = new TextBox { PlaceholderText = "Title", Text = defaultName };
            var urlBox = new TextBox { PlaceholderText = "Address", Text = defaultUrl };

            bool hasUrl = !string.IsNullOrWhiteSpace(defaultUrl);
            var expandedPanel = new StackPanel { Spacing = 8, Visibility = hasUrl ? Visibility.Collapsed : Visibility.Visible };
            expandedPanel.Children.Add(new TextBlock { Text = "Address:" });
            expandedPanel.Children.Add(urlBox);

            var moreOptionsButton = new HyperlinkButton { Content = hasUrl ? "More Options" : "Fewer Options", Padding = new Thickness(0, 4, 0, 0) };
            moreOptionsButton.Click += (s, e) =>
            {
                bool expanding = expandedPanel.Visibility == Visibility.Collapsed;
                expandedPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                moreOptionsButton.Content = expanding ? "Fewer Options" : "More Options";
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Add this page to:" });
            panel.Children.Add(locationCombo);
            panel.Children.Add(nameBox);
            panel.Children.Add(expandedPanel);
            panel.Children.Add(moreOptionsButton);

            var dialog = new PopupDialog
            {
                Title = "Add Bookmark",
                DialogContent = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel"
            };

            PopupDialogResult result = await dialog.ShowAsync(this);

            if (result == PopupDialogResult.Primary
                && !string.IsNullOrWhiteSpace(nameBox.Text)
                && !string.IsNullOrWhiteSpace(urlBox.Text))
            {
                if (locationCombo.SelectedItem is ComboBoxItem selected && selected.Tag is string tag && tag == "reading_list")
                {
                    _ = OrionBackend.AddReadingListEntry(ViewModel.ActiveProfileId, nameBox.Text, urlBox.Text);
                    ReadingListChanged.Notify();
                }
                else
                {
                    long parentFolderId = locationCombo.SelectedItem is ComboBoxItem sel && sel.Tag is long folderId
                        ? folderId : 0;
                    _ = OrionBackend.CreateBookmark(ViewModel.ActiveProfileId, nameBox.Text, urlBox.Text, parentFolderId);
                    BookmarksChanged.Notify();
                }
            }
        }

        private async System.Threading.Tasks.Task<StorageFile?> ShowSaveFilePickerAsync(
            string suggestedFileName, string fileTypeLabel, string fileExtension)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = suggestedFileName
            };
            picker.FileTypeChoices.Add(fileTypeLabel, [fileExtension]);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, Utils.GetWindowHandle(this));
            return await picker.PickSaveFileAsync();
        }

        private async System.Threading.Tasks.Task ShowSavePageDialog()
        {
            BrowserTab? activeTab = ViewModel.ActiveTab;
            if (activeTab == null)
                return;

            StorageFile? file = await ShowSaveFilePickerAsync(activeTab.Title, "Web Archive", ".webarchive");
            if (file == null)
                return;

            // TODO: implement actual save when backend supports it
        }

        private async System.Threading.Tasks.Task ShowExportAsPdfDialog()
        {
            BrowserTab? activeTab = ViewModel.ActiveTab;
            if (activeTab == null)
                return;

            StorageFile? file = await ShowSaveFilePickerAsync(activeTab.Title, "PDF", ".pdf");
            if (file == null)
                return;

            // TODO: implement actual export when backend supports it
        }

        private async System.Threading.Tasks.Task ShowExportBookmarksDialog()
        {
            StorageFile? file = await ShowSaveFilePickerAsync("bookmarks", "HTML", ".html");
            if (file == null)
                return;

            // TODO: implement actual export when backend supports it
        }

        private async System.Threading.Tasks.Task ShowImportPasswordsCsvDialog()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            picker.FileTypeFilter.Add(".csv");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, Utils.GetWindowHandle(this));

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            string csvText = await FileIO.ReadTextAsync(file);
            (bool success, int imported, int notImported) =
                OrionBackend.ImportPasswordItemsFromCsv(ViewModel.ActiveProfileId, csvText);

            string message = success
                ? $"Imported {imported} password(s)."
                  + (notImported > 0 ? $"\n{notImported} skipped (already exist with different password)." : "")
                : "Failed to import passwords.";

            await PopupDialog.ShowMessageAsync(this, "Import Passwords", message, closeButton: "OK");
        }

        private async System.Threading.Tasks.Task ShowExportPasswordsDialog()
        {
            StorageFile? file = await ShowSaveFilePickerAsync("passwords", "CSV", ".csv");
            if (file == null)
                return;

            // TODO: implement actual export when backend supports it
        }

        private static void AddFolderNodes(
            ComboBox combo,
            System.Collections.ObjectModel.ObservableCollection<BookmarkTreeNode> nodes,
            int depth,
            ref int defaultIndex,
            ref int comboIdx)
        {
            foreach (BookmarkTreeNode node in nodes.Where(n => n.IsFolder))
            {
                string indent = new(' ', depth * 3);
                combo.Items.Add(new ComboBoxItem { Content = indent + node.Title, Tag = node.EntryId });
                if (node.Entry.FolderType == 2)
                    defaultIndex = comboIdx;
                comboIdx++;
                AddFolderNodes(combo, node.Children, depth + 1, ref defaultIndex, ref comboIdx);
            }
        }
    }
}
