using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private TabGroup? _pinFolderGroup;
        private bool _syncingPinFolderExpansion;
        private BrowserTab[] _renderedFolderMembers = [];

        private void TrackPinFolderGroup()
        {
            TabGroup? current = ViewModel.ActiveGroup?.Model;
            if (current == _pinFolderGroup)
                return;

            StopPinFolderTracking();
            _pinFolderGroup = current;
            if (current != null)
            {
                current.PinFoldersChanged += OnPinFoldersChanged;
                current.FlattenedTabCollection.CollectionChanged += OnPinFolderTabTreeChanged;
            }
            OnPinFoldersChanged(structureChanged: true);
        }

        private void StopPinFolderTracking()
        {
            if (_pinFolderGroup is not { } group)
                return;
            group.PinFoldersChanged -= OnPinFoldersChanged;
            group.FlattenedTabCollection.CollectionChanged -= OnPinFolderTabTreeChanged;
            _pinFolderGroup = null;
        }

        private void OnPinFolderTabTreeChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            BrowserTab[] members = GetLiveFolderMembers();
            if (!_renderedFolderMembers.SequenceEqual(members))
                RenderPinFolderSections(members);
            RefreshFoldedPinList();
        }

        private BrowserTab[] GetLiveFolderMembers() => _pinFolderGroup?.Tabs
            .Where(tab => tab.IsInPinFolder && tab.Pin?.Scope == TabPinScope.Workspace).ToArray() ?? [];

        private void OnPinFoldersChanged(bool structureChanged)
        {
            if (structureChanged)
            {
                RenderPinFolderSections(GetLiveFolderMembers());
                RefreshFoldedPinList();
                if (ViewModel.ActiveTab is { } active)
                    VerticalTabStrip.SelectedItem = active.IsInPinFolder ? null : active;
            }
            else
            {
                SyncPinFolderExpansion();
            }
        }

        private void RefreshFoldedPinList()
        {
            FoldedPinFoldersButton.Visibility = _pinFolderGroup?.PinFolders.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            FoldedTabs.ItemsSource = _renderedFolderMembers.Length == 0
                ? ViewModel.FlattenedActiveGroupTabs
                : _pinFolderGroup?.FlattenedTabs.Where(t => !t.IsInPinFolder).ToArray();
        }

        private void RenderPinFolderSections(BrowserTab[] members)
        {
            _renderedFolderMembers = members;
            WorkspacePinFolderSections.Children.Clear();
            OpenTabsSectionLabel.Visibility = _pinFolderGroup?.PinFolders.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            if (_pinFolderGroup is not { } group)
                return;

            var liveTabs = members.ToDictionary(tab => tab.Id);
            int folderIndex = 0;
            foreach (WorkspacePinFolder folder in group.PinFolders)
            {
                var items = new StackPanel { Spacing = 2 };
                foreach (long tabId in folder.TabIds)
                {
                    if (liveTabs.TryGetValue(tabId, out BrowserTab? tab))
                        items.Children.Add(BuildPinFolderTabButton(tab));
                }
                if (items.Children.Count == 0)
                    items.Children.Add(new TextBlock { Text = "No pinned tabs", Margin = new Thickness(4, 3, 4, 6) });

                var folderMenu = new MenuFlyout();
                AddFolderActions(folderMenu.Items, group, folder, folderIndex++);
                var header = new TextBlock
                {
                    Text = folder.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                ToolTipService.SetToolTip(header, folder.Name);

                var expander = new Expander
                {
                    Tag = folder.Id,
                    Header = header,
                    Content = items,
                    ContextFlyout = folderMenu,
                    IsExpanded = folder.IsExpanded,
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetName(expander, folder.Name);
                expander.Expanding += (_, _) => PersistFolderExpansion(group, folder.Id, true);
                expander.Collapsed += (_, _) => PersistFolderExpansion(group, folder.Id, false);
                WorkspacePinFolderSections.Children.Add(expander);
            }
        }

        private Button BuildPinFolderTabButton(BrowserTab tab)
        {
            var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetBinding(TextBlock.TextProperty, new Binding { Source = tab, Path = new PropertyPath(nameof(BrowserTab.Title)) });
            var button = new Button
            {
                Content = title,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.SetBinding(AutomationProperties.NameProperty, new Binding { Source = tab, Path = new PropertyPath(nameof(BrowserTab.Title)) });
            button.Click += (_, _) => SwitchToTab(tab);
            var context = new MenuFlyout();
            context.Opening += (_, _) =>
            {
                context.Items.Clear();
                _ = BuildTabContextMenu(tab, context);
            };
            button.ContextFlyout = context;
            return button;
        }

        private void SyncPinFolderExpansion()
        {
            if (_pinFolderGroup is not { } group)
                return;
            _syncingPinFolderExpansion = true;
            try
            {
                foreach (Expander expander in WorkspacePinFolderSections.Children.OfType<Expander>())
                {
                    if (expander.Tag is Guid id && group.PinFolders.FirstOrDefault(f => f.Id == id) is { } folder)
                        expander.IsExpanded = folder.IsExpanded;
                }
            }
            finally
            {
                _syncingPinFolderExpansion = false;
            }
        }

        private void PersistFolderExpansion(TabGroup group, Guid folderId, bool expanded)
        {
            if (_syncingPinFolderExpansion || group != _pinFolderGroup)
                return;
            if (!ViewModel.SetPinFolderExpanded(group, folderId, expanded))
            {
                SyncPinFolderExpansion();
                ShowPinFolderWriteError();
            }
        }

        private void AddPinFolderMenu(MenuFlyout menu, BrowserTab tab, TabGroup group)
        {
            if (!MainWindowViewModel.CanFilePin(group, tab))
                return;

            var folders = new MenuFlyoutSubItem { Text = "Pin folder" };
            AddPinFolderAction(folders.Items, "New folder…", async () =>
            {
                string? name = await PromptPinFolderNameAsync("New pin folder", "");
                if (name != null && !ViewModel.CreatePinFolder(group, tab, name))
                    ShowPinFolderWriteError();
            });

            WorkspacePinFolder? current = group.PinFolders.FirstOrDefault(f => f.TabIds.Contains(tab.Id));
            foreach (WorkspacePinFolder folder in group.PinFolders.Where(f => f.Id != current?.Id))
            {
                AddPinFolderAction(folders.Items, $"Move to {folder.Name}", () =>
                    CheckPinFolderWrite(ViewModel.MovePinToFolder(group, tab, folder.Id)));
            }
            if (current != null)
            {
                int index = Array.IndexOf(current.TabIds, tab.Id);
                AddPinFolderAction(folders.Items, "Move pin up", () =>
                    CheckPinFolderWrite(ViewModel.ReorderPinInFolder(group, current.Id, tab.Id, -1)), index > 0);
                AddPinFolderAction(folders.Items, "Move pin down", () =>
                    CheckPinFolderWrite(ViewModel.ReorderPinInFolder(group, current.Id, tab.Id, 1)), index + 1 < current.TabIds.Length);
                AddPinFolderAction(folders.Items, "Remove pin from folder", () =>
                    CheckPinFolderWrite(ViewModel.MovePinToFolder(group, tab, null)));
            }
            menu.Items.Add(folders);
        }

        private void AddFolderActions(IList<MenuFlyoutItemBase> items, TabGroup group, WorkspacePinFolder folder, int index)
        {
            AddPinFolderAction(items, "Rename folder…", async () =>
            {
                string? name = await PromptPinFolderNameAsync("Rename pin folder", folder.Name);
                if (name != null && !ViewModel.RenamePinFolder(group, folder.Id, name))
                    ShowPinFolderWriteError();
            });
            AddPinFolderAction(items, "Move folder up", () =>
                CheckPinFolderWrite(ViewModel.ReorderPinFolder(group, folder.Id, -1)), index > 0);
            AddPinFolderAction(items, "Move folder down", () =>
                CheckPinFolderWrite(ViewModel.ReorderPinFolder(group, folder.Id, 1)), index + 1 < group.PinFolders.Count);
            AddPinFolderAction(items, "Unpack folder", () =>
                CheckPinFolderWrite(ViewModel.UnpackPinFolder(group, folder.Id)));
        }

        private static void AddPinFolderAction(IList<MenuFlyoutItemBase> items, string text, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            items.Add(item);
        }

        private async Task<string?> PromptPinFolderNameAsync(string title, string initialName)
        {
            var input = new TextBox { Header = "Folder name", Text = initialName, MaxLength = 80 };
            var dialog = new PopupDialog
            {
                Title = title,
                DialogContent = input,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
            };
            return await dialog.ShowAsync(this) == PopupDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text)
                ? input.Text : null;
        }

        private void FoldedPinFoldersMenu_Opening(object sender, object e)
        {
            FoldedPinFoldersMenu.Items.Clear();
            if (_pinFolderGroup is not { } group)
                return;

            var liveTabs = _renderedFolderMembers.ToDictionary(tab => tab.Id);
            int index = 0;
            foreach (WorkspacePinFolder folder in group.PinFolders)
            {
                var menu = new MenuFlyoutSubItem { Text = folder.Name };
                foreach (long id in folder.TabIds)
                {
                    if (liveTabs.TryGetValue(id, out BrowserTab? tab))
                        AddPinFolderAction(menu.Items, tab.Title, () => SwitchToTab(tab));
                }
                if (menu.Items.Count > 0)
                    menu.Items.Add(new MenuFlyoutSeparator());
                AddFolderActions(menu.Items, group, folder, index++);
                FoldedPinFoldersMenu.Items.Add(menu);
            }
        }

        private void CheckPinFolderWrite(bool success)
        {
            if (!success)
                ShowPinFolderWriteError();
        }

        private void ShowPinFolderWriteError() =>
            _ = PopupDialog.ShowMessageAsync(this, "Couldn’t update the pin folder", "Try the change again.");
    }
}
