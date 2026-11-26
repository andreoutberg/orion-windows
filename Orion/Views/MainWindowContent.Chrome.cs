using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private enum SidebarPresentation { Hidden, Folded, Expanded }

        private const double FoldedSidebarWidth = 48;
        private const double ExpandedSidebarWidth = 224;

        private readonly UISettings _chromeMotionSettings = new();
        private SidebarPresentation _sidebarPresentation;

        private void UpdateSidebarPresentation()
        {
            SidebarPresentation presentation = (ViewModel.ShowSidebar, ViewModel.TabLayout, ViewModel.IsChromeHiddenForFullscreen) switch
            {
                (true, _, _) => SidebarPresentation.Expanded,
                (false, TabLayout.vertical, false) => SidebarPresentation.Folded,
                _ => SidebarPresentation.Hidden,
            };
            double paneLength = presentation switch
            {
                SidebarPresentation.Expanded => ExpandedSidebarWidth,
                SidebarPresentation.Folded => FoldedSidebarWidth,
                _ => 0,
            };
            Visibility expandedVisibility = presentation == SidebarPresentation.Expanded
                ? Visibility.Visible : Visibility.Collapsed;

            // The CEF child HWND follows committed layout bounds. Fade only the rail contents.
            SidebarPaneColumn.Width = new GridLength(paneLength);
            SidebarPane.Visibility = presentation == SidebarPresentation.Hidden
                ? Visibility.Collapsed : Visibility.Visible;
            ExpandedSidebar.Visibility = expandedVisibility;
            FoldedSidebar.Visibility = presentation == SidebarPresentation.Folded
                ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceSwitcherLabel.Visibility = expandedVisibility;
            WorkspaceSwitcherChevron.Visibility = expandedVisibility;

            if (presentation != _sidebarPresentation && IsLoaded)
            {
                if (presentation == SidebarPresentation.Hidden)
                    TabGroupSwitcher.Close();
                if (presentation == SidebarPresentation.Expanded)
                    AnimateChromeOpacity(ExpandedSidebar, 0.62f, 1, 160);
                else if (presentation == SidebarPresentation.Folded)
                    AnimateChromeOpacity(FoldedSidebar, 0.62f, 1, 120);
            }
            _sidebarPresentation = presentation;
        }

        private void AnimateChromeOpacity(UIElement element, float from, float to, int milliseconds)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = to;
            if (!_chromeMotionSettings.AnimationsEnabled)
                return;

            Compositor compositor = visual.Compositor;
            ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            animation.InsertKeyFrame(0, from);
            animation.InsertKeyFrame(1, to, compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.22f, 1), new Vector2(0.36f, 1)));
            visual.StartAnimation(nameof(Visual.Opacity), animation);
        }

        private void FoldedTabs_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BrowserTab tab)
                SwitchToTab(tab);
        }

        private void FoldedTab_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: BrowserTab tab } target)
            {
                BuildTabContextMenu(tab).ShowAt(target);
                e.Handled = true;
            }
        }
    }
}
