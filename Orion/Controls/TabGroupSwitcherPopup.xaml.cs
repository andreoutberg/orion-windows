using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;

namespace Orion
{
    public sealed partial class TabGroupSwitcherPopup : UserControl
    {
        public event EventHandler<TabGroup>? GroupSelected;
        public event EventHandler? NewGroupRequested;
        public event EventHandler<GroupRenamedEventArgs>? GroupRenamed;
        public event EventHandler<TabGroup>? GroupsReordered;
        public event EventHandler<TabLayout>? TabLayoutChanged;

        /// <summary>
        /// Context flyout factory, forwarded to the inner TabGroupListPanel.
        /// </summary>
        public Func<TabGroup, MenuFlyout>? ContextFlyoutFactory
        {
            get => InnerGroupList.ContextFlyoutFactory;
            set => InnerGroupList.ContextFlyoutFactory = value;
        }

        public ObservableCollection<TabGroup>? SavedTabGroups
        {
            get => InnerGroupList.SavedTabGroups;
            set => InnerGroupList.SavedTabGroups = value;
        }

        public TabGroupSwitcherPopup() => InitializeComponent();

        public void ShowBelow(FrameworkElement anchor, TabGroup? unsavedGroup, TabGroup? selectedGroup,
            TabLayout layout, bool animationsEnabled)
        {
            InnerGroupList.UnsavedTabGroup = unsavedGroup;
            InnerGroupList.SelectedGroup = selectedGroup;
            TabLayoutPicker.SelectedValue = layout.ToString();
            SwitcherFlyout.AreOpenCloseAnimationsEnabled = animationsEnabled;
            SwitcherFlyout.ShowAt(anchor);
        }

        public void Close() => SwitcherFlyout.Hide();

        private void InnerGroupList_GroupSelected(object? sender, TabGroup e) => GroupSelected?.Invoke(this, e);

        private void InnerGroupList_GroupRenamed(object? sender, GroupRenamedEventArgs e) => GroupRenamed?.Invoke(this, e);

        private void InnerGroupList_GroupsReordered(object? sender, TabGroup e) => GroupsReordered?.Invoke(this, e);

        private void InnerGroupList_NewGroupRequested(object? sender, EventArgs e) => NewGroupRequested?.Invoke(this, EventArgs.Empty);

        private void TabLayoutPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabLayoutPicker.SelectedValue is string value && Enum.TryParse(value, out TabLayout layout))
                TabLayoutChanged?.Invoke(this, layout);
        }
    }
}
