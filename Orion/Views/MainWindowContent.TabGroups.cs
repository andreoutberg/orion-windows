using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private bool _switcherEventsWired;

        /// <summary>
        /// Wires the ContextFlyoutFactory on the sidebar tab groups panel.
        /// Called once during construction (after InitializeComponent).
        /// </summary>
        private void InitializeTabGroupContextMenus() => SidebarTabGroupsPanel.ContextFlyoutFactory =
                group => CreateTabGroupContextMenu(group, g => _ = ShowEditGroupDialog(g));

        private MenuFlyout CreateTabGroupContextMenu(TabGroup group, Action<TabGroup> onEdit)
        {
            var flyout = new MenuFlyout();

            // Opens the name + shortcut editor (macOS labels this "Edit…");
            // quick inline rename stays on double-tap in the group lists.
            var editItem = new MenuFlyoutItem { Text = group.IsSaved ? "Edit..." : "Save Group..." };
            editItem.Click += (s, e) => onEdit(group);
            flyout.Items.Add(editItem);

            if (group.IsSaved)
            {
                var duplicateItem = new MenuFlyoutItem { Text = "Duplicate Group" };
                duplicateItem.Click += (s, e) => ViewModel.DuplicateTabGroup(group);
                flyout.Items.Add(duplicateItem);

                var openInNewWindowItem = new MenuFlyoutItem { Text = "Open in a New Window" };
                openInNewWindowItem.Click += (s, e) => OpenTabGroupInNewWindow(group);
                flyout.Items.Add(openInNewWindowItem);
            }

            var deleteItem = new MenuFlyoutItem { Text = "Delete" };
            deleteItem.Click += (s, e) => _ = DeleteTabGroupAsync(group);
            flyout.Items.Add(deleteItem);

            return flyout;
        }

        private void OpenTabGroupInNewWindow(TabGroup group)
        {
            OrionBackend.PersistedTabGroup? persisted = ViewModel.DetachTabGroup(group);
            if (persisted == null)
                return;

            var window = new MainWindow(ViewModel.ActiveProfile, [persisted]);
            window.Activate();
        }

        /// <summary>
        /// Name + keyboard shortcut editor for a tab group (the macOS
        /// "Edit…" named-window dialog equivalent). The shortcut commits with
        /// the name on confirm; conflicts with another group block the capture.
        /// </summary>
        private async Task ShowEditGroupDialog(TabGroup group)
        {
            var nameBox = new TextBox { PlaceholderText = "Group name", Text = group.Name };
            var recorder = new ShortcutRecorderBox { HorizontalAlignment = HorizontalAlignment.Left };
            var error = new TextBlock
            {
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            };

            KeyCombo? pending = KeyCombo.TryParseGtkAccel(group.KeyboardShortcut, out KeyCombo current) ? current : null;
            // Only user edits are written back — a stored value this port cannot
            // parse (recorded on another port) survives a plain rename.
            bool shortcutEdited = false;
            recorder.ShowCombo(pending);
            recorder.ChordCaptured += (r, combo) =>
            {
                // Conflicts block, never steal (macOS parity): built-in
                // shortcuts first (they always win at dispatch), then other groups.
                string? conflict = EntityShortcuts.BuiltinUsing(combo) is string builtin
                    ? $"{combo.ToDisplayString()} is used by “{builtin}”"
                    : EntityShortcuts.GroupUsing(combo, ViewModel, exclude: group) != null
                        ? $"{combo.ToDisplayString()} is used by another tab group"
                        : null;
                if (conflict != null)
                {
                    error.Text = conflict;
                    error.Visibility = Visibility.Visible;
                    r.ShowCombo(pending);
                    return;
                }
                error.Visibility = Visibility.Collapsed;
                pending = combo;
                shortcutEdited = true;
                r.ShowCombo(combo);
            };
            recorder.ClearRequested += r =>
            {
                error.Visibility = Visibility.Collapsed;
                pending = null;
                shortcutEdited = true;
                r.ShowCombo(null);
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Group Name:" });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "Keyboard Shortcut:" });
            panel.Children.Add(recorder);
            panel.Children.Add(error);

            var dialog = new PopupDialog
            {
                Title = group.IsSaved ? "Rename Workspace" : "Name Workspace",
                DialogContent = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
            };
            if (await dialog.ShowAsync(this) != PopupDialogResult.Primary
                || string.IsNullOrWhiteSpace(nameBox.Text))
            {
                return;
            }

            string newName = nameBox.Text;
            TabGroup target = group;
            if (!group.IsSaved)
            {
                target = ViewModel.SaveUnsavedGroup(newName).Model;
            }
            else
            {
                group.Name = newName;
                OrionBackend.UpdateTabGroupName(group.Id, newName);
            }

            if (shortcutEdited)
            {
                string shortcut = pending?.ToGtkAccelString() ?? string.Empty;
                target.KeyboardShortcut = shortcut;
                OrionBackend.UpdateTabGroupKeyboardShortcut(target.Id, shortcut);
                EntityShortcuts.Refresh();
            }
        }

        private async Task<bool> DeleteTabGroupAsync(TabGroup group)
        {
            int tabCount = group.FlattenedTabCollection.Count;
            string groupLabel = group.IsSaved ? $"\u201c{group.Name}\u201d" : "this tab group";
            string message = $"Are you sure you want to delete {groupLabel}?\n\n"
                + $"This would close {tabCount} {(tabCount == 1 ? "tab" : "tabs")}.";
            bool confirmed = await PopupDialog.ShowConfirmAsync(
                this, $"Delete {groupLabel}?",
                message: message,
                confirmButton: "Delete");
            return confirmed && ViewModel.DeleteTabGroup(group);
        }

        // --- Tab Group Switcher Popover ---

        private void TabGroupSwitcherButton_Click(object sender, RoutedEventArgs e) =>
            ShowTabGroupSwitcher(TabGroupSwitcherButton);

        private void ShowTabGroupSwitcher(FrameworkElement anchor)
        {
            if (!_switcherEventsWired)
            {
                TabGroupSwitcher.ContextFlyoutFactory =
                    group => CreateTabGroupContextMenu(group, g => _ = ShowEditGroupDialog(g));
                TabGroupSwitcher.SavedTabGroups = ViewModel.SavedTabGroups;
                TabGroupSwitcher.GroupSelected += OnSwitcherGroupSelected;
                TabGroupSwitcher.NewGroupRequested += OnSwitcherNewGroup;
                TabGroupSwitcher.GroupRenamed += OnSwitcherGroupRenamed;
                TabGroupSwitcher.GroupsReordered += SidebarGroupsReordered;
                TabGroupSwitcher.TabLayoutChanged += OnWorkspaceTabLayoutChanged;
                _switcherEventsWired = true;
            }

            TabGroupSwitcher.ShowBelow(anchor, ViewModel.UnsavedTabGroup, ViewModel.ActiveGroup?.Model,
                ViewModel.TabLayout, _chromeMotionSettings.AnimationsEnabled);
        }

        private void OnSwitcherGroupSelected(object? sender, TabGroup group)
        {
            ViewModel.SelectTabGroup(group);
            TabGroupSwitcher.Close();
        }

        private void OnSwitcherNewGroup(object? sender, EventArgs e)
        {
            TabGroupSwitcher.Close();
            _ = ViewModel.CreateTabGroup();
        }

        private void OnSwitcherGroupRenamed(object? sender, GroupRenamedEventArgs args) => ApplyGroupRename(args.Group, args.NewName);

        // --- Sidebar Tab Groups Panel ---

        private void SidebarGroupItem_Selected(object? sender, TabGroup group) => ViewModel.SelectTabGroup(group);

        private void SidebarNewTabGroup_Requested(object? sender, EventArgs e) => _ = ViewModel.CreateTabGroup();

        private void SidebarGroupRenamed(object? sender, GroupRenamedEventArgs args) => ApplyGroupRename(args.Group, args.NewName);

        private void ApplyGroupRename(TabGroup group, string newName)
        {
            if (!group.IsSaved)
            {
                _ = ViewModel.SaveUnsavedGroup(newName);
            }
            else
            {
                group.Name = newName;
                OrionBackend.UpdateTabGroupName(group.Id, newName);
            }
        }

        private void SidebarGroupsReordered(object? sender, TabGroup group) => PersistTabGroupMove(group);

        private void PersistTabGroupMove(TabGroup group)
        {
            if (group.Id == 0)
                return;

            ObservableCollection<TabGroup> groups = ViewModel.SavedTabGroups;
            int index = groups.IndexOf(group);
            if (index < 0)
                return;

            long nextId = index + 1 < groups.Count ? groups[index + 1].Id : 0;
            OrionBackend.MoveTabGroup(group.Id, nextId);
        }
    }
}
