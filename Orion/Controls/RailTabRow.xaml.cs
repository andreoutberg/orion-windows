using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;

namespace Orion
{
    public sealed partial class RailTabRow : UserControl
    {
        private TreeViewItem? _item;
        private bool _pointerOver;

        public event EventHandler<BrowserTab>? CloseRequested;

        public RailTabRow() => InitializeComponent();

        private void Row_Loaded(object sender, RoutedEventArgs e)
        {
            if (_item != null || FindTabItem(this) is not { } item)
                return;

            _item = item;
            item.PointerEntered += Item_PointerEntered;
            item.PointerExited += Item_PointerExited;
            item.GotFocus += Item_FocusChanged;
            item.LostFocus += Item_FocusChanged;
            UpdateCloseButton();
        }

        private void Row_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_item is not { } item)
                return;

            item.PointerEntered -= Item_PointerEntered;
            item.PointerExited -= Item_PointerExited;
            item.GotFocus -= Item_FocusChanged;
            item.LostFocus -= Item_FocusChanged;
            _item = null;
            _pointerOver = false;
            CloseTabButton.Visibility = Visibility.Collapsed;
        }

        private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _pointerOver = true;
            UpdateCloseButton();
        }

        private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerOver = false;
            UpdateCloseButton();
        }

        private void Item_FocusChanged(object sender, RoutedEventArgs e) => UpdateCloseButton();

        private void UpdateCloseButton()
        {
            // Focus events bubble asynchronously, so read the current focus for this window.
            bool hasKeyboardFocus = XamlRoot is { } root
                && FocusManager.GetFocusedElement(root) is UIElement { FocusState: FocusState.Keyboard } focused
                && FindTabItem(focused) == _item;
            CloseTabButton.Visibility = _pointerOver || hasKeyboardFocus ? Visibility.Visible : Visibility.Collapsed;
        }

        private static TreeViewItem? FindTabItem(DependencyObject element)
        {
            for (DependencyObject? candidate = element; candidate != null; candidate = VisualTreeHelper.GetParent(candidate))
            {
                if (candidate is TreeViewItem item)
                    return item;
            }
            return null;
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is BrowserTab { IsPinned: false } tab)
                CloseRequested?.Invoke(this, tab);
        }
    }
}
