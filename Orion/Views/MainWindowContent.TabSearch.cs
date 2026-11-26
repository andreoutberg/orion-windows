using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using Windows.System;

namespace Orion
{
    public sealed record TabSearchMatch(BrowserTab Tab, TabGroup Group)
    {
        public string Title => string.IsNullOrWhiteSpace(Tab.Title) ? Tab.Url : Tab.Title;

        public override string ToString() => Title;

        public string Context
        {
            get
            {
                if (!Uri.TryCreate(Tab.Url, UriKind.Absolute, out Uri? uri))
                    return Group.DisplayName;

                string site = string.IsNullOrEmpty(uri.Host) ? uri.Scheme : uri.Host;
                if (uri.IsFile)
                    site = "Local file";
                return $"{Group.DisplayName}  ·  {site}";
            }
        }
    }

    public sealed partial class MainWindowContent
    {
        private void ShowTabSearch()
        {
            if (!ViewModel.IsChromeHiddenForFullscreen)
                TabSearchFlyout.ShowAt(TabSearchButton);
        }

        private void TabSearchFlyout_Opened(object sender, object e)
        {
            TabSearchBox.Text = string.Empty;
            UpdateTabSearch();
            AnimateChromeOpacity(TabSearchResults, 0.5f, 1, 140);
            _ = TabSearchBox.Focus(FocusState.Keyboard);
        }

        private void TabSearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTabSearch();

        private void UpdateTabSearch()
        {
            string query = TabSearchBox.Text.Trim();
            TabSearchMatch[] matches = [.. ViewModel.TabGroups
                .OrderByDescending(group => group == ViewModel.ActiveGroup)
                .SelectMany(group => group.Model.FlattenedTabs.Select(tab => new TabSearchMatch(tab, group.Model)))
                .Where(match => match.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || match.Tab.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || match.Group.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(30)];
            TabSearchResults.ItemsSource = matches;
            TabSearchResults.SelectedIndex = matches.Length == 0 ? -1 : 0;
            TabSearchEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TabSearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                if (TabSearchResults.SelectedItem is TabSearchMatch match)
                    ActivateSearchMatch(match);
                e.Handled = true;
                return;
            }
            if (e.Key is not (VirtualKey.Up or VirtualKey.Down) || TabSearchResults.Items.Count == 0)
                return;

            int offset = e.Key == VirtualKey.Up ? -1 : 1;
            int index = Math.Clamp(TabSearchResults.SelectedIndex + offset, 0, TabSearchResults.Items.Count - 1);
            TabSearchResults.SelectedIndex = index;
            TabSearchResults.ScrollIntoView(TabSearchResults.Items[index]);
            e.Handled = true;
        }

        private void TabSearchResults_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TabSearchMatch match)
                ActivateSearchMatch(match);
        }

        private void ActivateSearchMatch(TabSearchMatch match)
        {
            TabSearchFlyout.Hide();
            if (ViewModel.ActiveGroup?.Model != match.Group)
                ViewModel.SelectTabGroup(match.Group);
            SwitchToTab(match.Tab);
        }
    }
}
