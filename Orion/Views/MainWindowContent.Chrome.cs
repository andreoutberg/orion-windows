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

        private readonly UISettings _chromeMotionSettings = new();
        private SidebarPresentation _sidebarPresentation;

        private void UpdateSidebarPresentation()
        {
            bool canFold = ViewModel.TabLayout == TabLayout.vertical
                && !ViewModel.IsChromeHiddenForFullscreen;
            SidebarPresentation presentation = ViewModel.ShowSidebar
                ? SidebarPresentation.Expanded
                : canFold ? SidebarPresentation.Folded : SidebarPresentation.Hidden;

            SidebarSplitView.DisplayMode = canFold
                ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.Inline;
            ExpandedSidebar.Visibility = presentation == SidebarPresentation.Expanded
                ? Visibility.Visible : Visibility.Collapsed;
            FoldedSidebar.Visibility = presentation == SidebarPresentation.Folded
                ? Visibility.Visible : Visibility.Collapsed;

            if (presentation != _sidebarPresentation && IsLoaded)
            {
                if (presentation == SidebarPresentation.Expanded)
                    AnimateChromeOpacity(ExpandedSidebar, 0.35f, 1, 160);
                else if (presentation == SidebarPresentation.Folded)
                    AnimateChromeOpacity(FoldedSidebar, 0.35f, 1, 140);
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

        private void ExpandSidebar_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarPage(SidebarPage.tabs);
            ViewModel.ShowSidebar = true;
            _ = PaneToggleButton.Focus(FocusState.Programmatic);
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
