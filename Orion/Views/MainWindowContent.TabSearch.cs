using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using Windows.System;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private Control? _tabSearchPreviousFocus;

        private void ShowTabSearch()
        {
            if (ViewModel.IsChromeHiddenForFullscreen || TabSearchFlyout.IsOpen)
                return;

            _tabSearchPreviousFocus = FocusManager.GetFocusedElement(XamlRoot) as Control;
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

        private void TabSearchFlyout_Closed(object sender, object e)
        {
            Control? previousFocus = _tabSearchPreviousFocus;
            _tabSearchPreviousFocus = null;
            if (previousFocus?.XamlRoot == XamlRoot)
                _ = previousFocus?.Focus(FocusState.Keyboard);
        }

        private void UpdateTabSearch()
        {
            TabSearchMatch[] matches = [.. TabSearchMatch.Find(ViewModel.TabGroups.Select(group => group.Model),
                ViewModel.ActiveGroup?.Model, TabSearchBox.Text).Take(30)];
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
            if (!ViewModel.TabGroups.Any(group => group.Model == match.Group)
                || (match.Tab is { } stale && !match.Group.TabExists(stale)))
            {
                UpdateTabSearch();
                return;
            }

            _tabSearchPreviousFocus = null;
            TabSearchFlyout.Hide();
            if (ViewModel.ActiveGroup?.Model != match.Group)
                ViewModel.SelectTabGroup(match.Group);
            if (match.Tab is { } tab)
                SwitchToTab(tab);
        }
    }
}
