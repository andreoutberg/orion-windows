using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Orion
{
    public partial class MainWindowViewModel
    {
        private const string PinFoldersSettingsRealm = "settings::tabs";
        private const string PinFoldersDefaultValue = "";

        private static string PinFoldersSettingName(TabGroup workspace) =>
            $"workspace_pin_folders_{workspace.Id.ToString(CultureInfo.InvariantCulture)}";

        private string ReadPinFoldersSetting(TabGroup workspace)
        {
            string name = PinFoldersSettingName(workspace);
            return OrionBackend.GetStringSetting(_profileId, $"{PinFoldersSettingsRealm}::{name}",
                PinFoldersSettingsRealm, name, PinFoldersDefaultValue);
        }

        private static bool CanUsePinFolders(TabGroup workspace) =>
            workspace.IsSaved && !workspace.IsPrivate && workspace.Id != 0;

        internal static bool CanFilePin(TabGroup workspace, BrowserTab tab) =>
            CanUsePinFolders(workspace) && tab.Id != 0 && tab.GroupId == workspace.Id
                && tab.Pin?.Scope == TabPinScope.Workspace && workspace.Tabs.Contains(tab);

        private void RestorePinFolders(TabGroup workspace)
        {
            if (!CanUsePinFolders(workspace))
                return;

            string stored = ReadPinFoldersSetting(workspace);
            if (string.IsNullOrEmpty(stored))
                return;

            try
            {
                WorkspacePinFolderState? state = JsonSerializer.Deserialize<WorkspacePinFolderState>(stored);
                if (state?.Version != WorkspacePinFolderState.CurrentVersion || state.Folders == null)
                    return;

                WorkspacePinFolderState repaired = state.Repair(workspace.Tabs
                    .Where(tab => CanFilePin(workspace, tab)).Select(tab => tab.Id));
                workspace.ApplyPinFolders(repaired.Folders, structureChanged: true);
                if (JsonSerializer.Serialize(state) != JsonSerializer.Serialize(repaired))
                    _ = WritePinFolders(workspace, repaired.Folders);
            }
            catch (JsonException)
            {
                OrionBackend.LogDebug("Ignoring invalid workspace pin folder settings");
            }
        }

        private bool WritePinFolders(TabGroup workspace, WorkspacePinFolder[] folders)
        {
            string name = PinFoldersSettingName(workspace);
            string next = folders.Length == 0 ? PinFoldersDefaultValue
                : JsonSerializer.Serialize(new WorkspacePinFolderState(WorkspacePinFolderState.CurrentVersion, folders));
            OrionBackend.SetStringSetting(_profileId, $"{PinFoldersSettingsRealm}::{name}",
                PinFoldersSettingsRealm, name, next, PinFoldersDefaultValue);
            return ReadPinFoldersSetting(workspace) == next;
        }

        private bool SavePinFolders(TabGroup workspace, WorkspacePinFolder[] folders, bool structureChanged = true)
        {
            if (!WritePinFolders(workspace, folders))
                return false;
            workspace.ApplyPinFolders(folders, structureChanged);
            return true;
        }

        internal bool CreatePinFolder(TabGroup workspace, BrowserTab tab, string name)
        {
            if (!CanFilePin(workspace, tab) || string.IsNullOrWhiteSpace(name))
                return false;

            WorkspacePinFolder newFolder = new(Guid.NewGuid(), name.Trim(), true, [tab.Id]);
            return SavePinFolders(workspace, [.. WithoutPin(workspace, tab.Id), newFolder]);
        }

        internal bool MovePinToFolder(TabGroup workspace, BrowserTab tab, Guid? folderId)
        {
            if (!CanFilePin(workspace, tab) || (folderId != null && !workspace.PinFolders.Any(f => f.Id == folderId)))
                return false;
            if (workspace.PinFolders.FirstOrDefault(f => f.TabIds.Contains(tab.Id))?.Id == folderId)
                return true;

            WorkspacePinFolder[] next = WithoutPin(workspace, tab.Id);
            if (folderId is Guid destination)
            {
                int index = Array.FindIndex(next, folder => folder.Id == destination);
                next[index] = next[index] with { TabIds = [.. next[index].TabIds, tab.Id] };
            }
            return SavePinFolders(workspace, next);
        }

        internal bool SetPinFolderExpanded(TabGroup workspace, Guid folderId, bool expanded) =>
            UpdatePinFolder(workspace, folderId, folder => folder with { IsExpanded = expanded }, structureChanged: false);

        internal bool RenamePinFolder(TabGroup workspace, Guid folderId, string name) =>
            !string.IsNullOrWhiteSpace(name) && UpdatePinFolder(workspace, folderId, folder => folder with { Name = name.Trim() });

        internal bool UnpackPinFolder(TabGroup workspace, Guid folderId) =>
            CanUsePinFolders(workspace) && workspace.PinFolders.Any(f => f.Id == folderId)
                && SavePinFolders(workspace, workspace.PinFolders.Where(folder => folder.Id != folderId).ToArray());

        internal bool ReorderPinFolder(TabGroup workspace, Guid folderId, int offset)
        {
            if (!CanUsePinFolders(workspace))
                return false;
            WorkspacePinFolder[] folders = workspace.PinFolders.ToArray();
            int index = Array.FindIndex(folders, folder => folder.Id == folderId);
            return MoveOne(folders, index, offset) && SavePinFolders(workspace, folders);
        }

        internal bool ReorderPinInFolder(TabGroup workspace, Guid folderId, long tabId, int offset)
        {
            WorkspacePinFolder? folder = workspace.PinFolders.FirstOrDefault(f => f.Id == folderId);
            if (folder == null)
                return false;
            long[] tabs = [.. folder.TabIds];
            return MoveOne(tabs, Array.IndexOf(tabs, tabId), offset)
                && UpdatePinFolder(workspace, folderId, current => current with { TabIds = tabs });
        }

        private bool UpdatePinFolder(TabGroup workspace, Guid folderId, Func<WorkspacePinFolder, WorkspacePinFolder> update,
            bool structureChanged = true)
        {
            if (!CanUsePinFolders(workspace))
                return false;
            WorkspacePinFolder[] next = workspace.PinFolders.ToArray();
            int index = Array.FindIndex(next, folder => folder.Id == folderId);
            if (index < 0)
                return false;
            WorkspacePinFolder updated = update(next[index]);
            if (updated == next[index])
                return true;
            next[index] = updated;
            return SavePinFolders(workspace, next, structureChanged);
        }

        private static bool MoveOne<T>(T[] items, int index, int offset)
        {
            int target = index + offset;
            if (index < 0 || target < 0 || target >= items.Length)
                return false;
            (items[index], items[target]) = (items[target], items[index]);
            return true;
        }

        private static WorkspacePinFolder[] WithoutPin(TabGroup workspace, long tabId) =>
            workspace.PinFolders.Select(folder => folder.TabIds.Contains(tabId)
                ? folder with { TabIds = folder.TabIds.Where(id => id != tabId).ToArray() }
                : folder).ToArray();

        private void DetachPinFromFolder(TabGroup workspace, BrowserTab tab)
        {
            if (tab.IsInPinFolder)
            {
                WorkspacePinFolder[] next = WithoutPin(workspace, tab.Id);
                // The pin write has already succeeded. Repair the live projection
                // even on a settings write failure. Restore validates IDs again.
                if (!SavePinFolders(workspace, next))
                    workspace.ApplyPinFolders(next, structureChanged: true);
            }
        }
    }
}
