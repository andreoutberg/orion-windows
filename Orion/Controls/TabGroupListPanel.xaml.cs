using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Orion
{
    public record GroupRenamedEventArgs(TabGroup Group, string NewName);

    public sealed partial class TabGroupListPanel : UserControl
    {
        public static readonly DependencyProperty UnsavedTabGroupProperty =
            DependencyProperty.Register(
                nameof(UnsavedTabGroup),
                typeof(TabGroup),
                typeof(TabGroupListPanel),
                new PropertyMetadata(null, OnUnsavedTabGroupChanged));

        public static readonly DependencyProperty SavedTabGroupsProperty =
            DependencyProperty.Register(
                nameof(SavedTabGroups),
                typeof(ObservableCollection<TabGroup>),
                typeof(TabGroupListPanel),
                new PropertyMetadata(null, OnSavedTabGroupsChanged));

        public static readonly DependencyProperty SelectedGroupProperty =
            DependencyProperty.Register(
                nameof(SelectedGroup),
                typeof(TabGroup),
                typeof(TabGroupListPanel),
                new PropertyMetadata(null, OnSelectedGroupChanged));

        public TabGroup? UnsavedTabGroup
        {
            get => (TabGroup?)GetValue(UnsavedTabGroupProperty);
            set => SetValue(UnsavedTabGroupProperty, value);
        }

        public ObservableCollection<TabGroup>? SavedTabGroups
        {
            get => (ObservableCollection<TabGroup>?)GetValue(SavedTabGroupsProperty);
            set => SetValue(SavedTabGroupsProperty, value);
        }

        public TabGroup? SelectedGroup
        {
            get => (TabGroup?)GetValue(SelectedGroupProperty);
            set => SetValue(SelectedGroupProperty, value);
        }

        public event EventHandler<TabGroup>? GroupSelected;
        public event EventHandler? NewGroupRequested;
        public event EventHandler<GroupRenamedEventArgs>? GroupRenamed;
        public event EventHandler<TabGroup>? GroupsReordered;

        /// <summary>
        /// Factory for creating context flyouts. Set by the parent to provide
        /// a single shared context menu builder for all tab group surfaces.
        /// </summary>
        public Func<TabGroup, MenuFlyout>? ContextFlyoutFactory { get; set; }

        private TabGroup? _editingGroup;
        private bool _commitOnLostFocus = true;

        public TabGroupListPanel() => InitializeComponent();

        // --- Inline editing ---

        public void BeginEdit(TabGroup group)
        {
            CancelEdit();

            if (group == UnsavedTabGroup)
            {
                _editingGroup = group;
                UnsavedEditBox.Text = group.Name;
                UnsavedGroupText.Visibility = Visibility.Collapsed;
                UnsavedEditPanel.Visibility = Visibility.Visible;
                _ = UnsavedEditBox.Focus(FocusState.Programmatic);
                UnsavedEditBox.SelectAll();
                return;
            }

            var container = SavedGroupsList.ContainerFromItem(group) as ListViewItem;
            if (container == null)
                return;

            (FrameworkElement? displayPanel, StackPanel? editPanel, TextBox? editBox, TextBlock? shortcutLabel) = FindEditElements(container);
            if (displayPanel == null || editPanel == null || editBox == null)
                return;

            _editingGroup = group;
            editBox.Text = group.Name;
            displayPanel.Visibility = Visibility.Collapsed;
            shortcutLabel?.Visibility = Visibility.Collapsed;
            editPanel.Visibility = Visibility.Visible;
            editBox.KeyDown += EditBox_KeyDown;
            editBox.LostFocus += EditBox_LostFocus;
            _ = editBox.Focus(FocusState.Programmatic);
            editBox.SelectAll();
        }

        private void CommitEdit()
        {
            if (_editingGroup == null)
                return;

            TabGroup group = _editingGroup;
            string newName;

            if (group == UnsavedTabGroup)
            {
                newName = UnsavedEditBox.Text.Trim();
                EndUnsavedEdit();
            }
            else
            {
                var container = SavedGroupsList.ContainerFromItem(group) as ListViewItem;
                if (container == null)
                {
                    _editingGroup = null;
                    return;
                }

                (FrameworkElement? displayPanel, StackPanel? editPanel, TextBox? editBox, TextBlock? shortcutLabel) = FindEditElements(container);
                if (editBox == null)
                {
                    _editingGroup = null;
                    return;
                }

                newName = editBox.Text.Trim();
                EndSavedEdit(displayPanel!, editPanel!, editBox, shortcutLabel);
            }

            _editingGroup = null;

            if (newName.Length > 0)
                GroupRenamed?.Invoke(this, new GroupRenamedEventArgs(group, newName));
        }

        private void CancelEdit()
        {
            if (_editingGroup == null)
                return;

            TabGroup group = _editingGroup;
            _editingGroup = null;

            if (group == UnsavedTabGroup)
            {
                EndUnsavedEdit();
            }
            else
            {
                var container = SavedGroupsList.ContainerFromItem(group) as ListViewItem;
                if (container == null)
                    return;

                (FrameworkElement? displayPanel, StackPanel? editPanel, TextBox? editBox, TextBlock? shortcutLabel) = FindEditElements(container);
                if (displayPanel != null && editPanel != null && editBox != null)
                    EndSavedEdit(displayPanel, editPanel, editBox, shortcutLabel);
            }
        }

        private void EndUnsavedEdit()
        {
            UnsavedEditPanel.Visibility = Visibility.Collapsed;
            UnsavedGroupText.Visibility = Visibility.Visible;
        }

        private void EndSavedEdit(FrameworkElement displayPanel, StackPanel editPanel, TextBox editBox,
            TextBlock? shortcutLabel)
        {
            editBox.KeyDown -= EditBox_KeyDown;
            editBox.LostFocus -= EditBox_LostFocus;
            editPanel.Visibility = Visibility.Collapsed;
            displayPanel.Visibility = Visibility.Visible;
            shortcutLabel?.Visibility = Visibility.Visible;
        }

        private void EditBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                _commitOnLostFocus = false;
                CommitEdit();
                _commitOnLostFocus = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                _commitOnLostFocus = false;
                CancelEdit();
                _commitOnLostFocus = true;
            }
        }

        private void EditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_commitOnLostFocus)
                CommitEdit();
        }

        private static (FrameworkElement? display, StackPanel? edit, TextBox? editBox, TextBlock? shortcutLabel) FindEditElements(
            ListViewItem container)
        {
            // The DataTemplate root is a Grid whose child order is a contract:
            // [0] = display panel (TextBlock), [1] = edit panel (TextBox),
            // [2] = keyboard shortcut label (hidden while editing)
            Grid? grid = FindDescendant<Grid>(container);
            if (grid == null || grid.Children.Count < 2)
                return (null, null, null, null);

            var display = grid.Children[0] as FrameworkElement;
            var edit = grid.Children[1] as StackPanel;
            TextBox? editBox = edit != null ? FindDescendant<TextBox>(edit) : null;
            TextBlock? shortcutLabel = grid.Children.Count > 2 ? grid.Children[2] as TextBlock : null;
            return (display, edit, editBox, shortcutLabel);
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;
                T? descendant = FindDescendant<T>(child);
                if (descendant != null)
                    return descendant;
            }
            return null;
        }

        // --- UnsavedTabGroup property ---

        private static void OnUnsavedTabGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TabGroupListPanel panel)
                return;

            if (e.OldValue is TabGroup oldGroup)
                oldGroup.PropertyChanged -= panel.OnUnsavedGroupPropertyChanged;
            if (e.NewValue is TabGroup newGroup)
                newGroup.PropertyChanged += panel.OnUnsavedGroupPropertyChanged;

            panel.UpdateUnsavedButton();
            panel.UpdateSeparatorVisibility();
        }

        private void OnUnsavedGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabGroup.DisplayName))
                UpdateUnsavedButton();
        }

        private void UpdateUnsavedButton()
        {
            if (UnsavedTabGroup != null)
            {
                UnsavedGroupButton.Visibility = Visibility.Visible;
                UnsavedGroupText.Text = UnsavedTabGroup.DisplayName;
                UnsavedGroupButton.ContextFlyout = ContextFlyoutFactory?.Invoke(UnsavedTabGroup);
            }
            else
            {
                UnsavedGroupButton.Visibility = Visibility.Collapsed;
            }

            UpdateUnsavedButtonHighlight();
        }

        // --- SavedTabGroups property ---

        private static void OnSavedTabGroupsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TabGroupListPanel panel)
                return;

            if (e.OldValue is ObservableCollection<TabGroup> oldColl)
                oldColl.CollectionChanged -= panel.OnSavedGroupCollectionChanged;
            if (e.NewValue is ObservableCollection<TabGroup> newColl)
                newColl.CollectionChanged += panel.OnSavedGroupCollectionChanged;

            panel.SavedGroupsList.ItemsSource = e.NewValue;
            panel.UpdateSeparatorVisibility();
            panel.SyncSelection();
        }

        private void OnSavedGroupCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateSeparatorVisibility();
            SyncSelection();
        }

        private void SavedGroupsList_ContainerContentChanging(ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            if (args.Item is TabGroup group && ContextFlyoutFactory != null)
                args.ItemContainer.ContextFlyout = ContextFlyoutFactory(group);
        }

        // --- SelectedGroup property ---

        private static void OnSelectedGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TabGroupListPanel panel)
                return;

            panel.SyncSelection();
        }

        private void SyncSelection()
        {
            TabGroup? selected = SelectedGroup;

            if (selected != null && SavedTabGroups?.Contains(selected) == true)
                SavedGroupsList.SelectedItem = selected;
            else
                SavedGroupsList.SelectedItem = null;

            UpdateUnsavedButtonHighlight();
        }

        private void UpdateUnsavedButtonHighlight()
        {
            bool isUnsavedSelected = UnsavedTabGroup != null && SelectedGroup == UnsavedTabGroup;
            UnsavedGroupButton.Background = isUnsavedSelected
                ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.Transparent);
        }

        private void UpdateSeparatorVisibility()
        {
            bool show = UnsavedTabGroup != null && (SavedTabGroups?.Count ?? 0) > 0;
            SectionSeparator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- Event handlers ---

        private void UnsavedGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (UnsavedTabGroup != null)
                GroupSelected?.Invoke(this, UnsavedTabGroup);
        }

        private void SavedGroupsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_editingGroup != null)
                return;
            if (e.ClickedItem is TabGroup group)
                GroupSelected?.Invoke(this, group);
        }

        private void SavedGroupsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement fe)
            {
                // Walk up to find the ListViewItem, then get the data item
                ListViewItem? item = Utils.GetParent<ListViewItem>(fe);
                if (item != null && SavedGroupsList.ItemFromContainer(item) is TabGroup group)
                    BeginEdit(group);
            }
        }

        private void SavedGroupsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            foreach (object item in args.Items)
            {
                if (item is TabGroup group)
                    GroupsReordered?.Invoke(this, group);
            }
        }

        private void NewGroupButton_Click(object sender, RoutedEventArgs e) => NewGroupRequested?.Invoke(this, EventArgs.Empty);

    }
}
