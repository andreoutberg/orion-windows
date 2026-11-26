using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Orion
{
    /// <summary>
    /// No-op stub replacing OrionBackend for unit tests.
    /// Only methods/types referenced by compiled source files (ViewModels, Models) need stubs here.
    /// </summary>
    public static class OrionBackend
    {
        private static long _nextId = 1;

        // ---- Nested record types used by MainWindowViewModel ----

        public record PersistedTabGroup(long Id, long ProfileId, string Name,
            bool IsTabGroup, long SelectedTabId, bool HasUiWindow, bool IsPrivate,
            string KeyboardShortcut = "");

        public record PersistedTab(long Id, long TabGroupId, string Name, string Uri,
            bool IsPinned, long ParentTabId, bool IsCollapsed);

        public record RecentlyClosedTab(long Id, long ProfileId, string Name, string Uri,
            DateTime ClosedAt, long WindowId);

        // Mirrors Interop/OrionBackend.cs (values from OpenDisposition in browser_engine_types.h)
        public enum OpenDisposition
        {
            NewForegroundTab = 0,
            NewBackgroundTab = 1,
            NewWindow = 2,
        }

        // ---- Settings (no-op) ----

        public static readonly AsyncLocal<Dictionary<string, bool>?> BoolSettingOverrides = new();

        public static bool GetBoolSetting(long profileId, string key, string realm, string keyName, bool defaultValue) =>
            BoolSettingOverrides.Value != null && BoolSettingOverrides.Value.TryGetValue(key, out bool v) ? v : defaultValue;

        public static readonly AsyncLocal<Dictionary<string, int>?> IntSettingOverrides = new();

        public static int GetIntSetting(long profileId, string key, string realm, string keyName, int defaultValue) =>
            IntSettingOverrides.Value != null && IntSettingOverrides.Value.TryGetValue(key, out int v) ? v : defaultValue;

        private static readonly Dictionary<(long ProfileId, string Key), string> _stringSettings = [];

        public static string GetStringSetting(long profileId, string key, string realm, string keyName, string defaultValue) =>
            _stringSettings.GetValueOrDefault((profileId, key), defaultValue);

        public static void SetStringSetting(long profileId, string key, string realm, string keyName, string value, string defaultValue) =>
            _stringSettings[(profileId, key)] = value;

        public static void LogDebug(string message) => Console.WriteLine($"[DEBUG] {message}");

        /// <summary>
        /// Per-test sink recording SetBoolSetting calls so tests can assert on
        /// persistence behavior. AsyncLocal keeps it isolated per execution
        /// context, so parallel test classes never observe each other's writes.
        /// A test installs its own list; contexts without a sink record nothing.
        /// </summary>
        public static readonly AsyncLocal<List<(long ProfileId, string Key, bool Value)>?> BoolSettingWriteSink = new();

        public static void SetBoolSetting(long profileId, string key, string realm, string keyName, bool value, bool defaultValue) =>
            BoolSettingWriteSink.Value?.Add((profileId, key, value));

        public static void SetIntSetting(long profileId, string key, string realm, string keyName, int value, int defaultValue) { }

        // ---- Profiles ----

        public static List<Profile> GetProfiles() => [];

        public static long GetCurrentProfileId() => 0;

        public static bool IsPrivateSession => false;

        public static string GetAppSetting(string key, string defaultValue) => defaultValue;

        public static void SetAppSetting(string key, string value, string type = "text") { }

        // ---- Tab Groups ----

        public static long GetSharedPinnedTabsWindowId(long profileId) => -profileId;

        public static long CreateTabGroupEntry(long profileId, bool isTabGroup, bool hasUiWindow) => Interlocked.Increment(ref _nextId);

        public static List<PersistedTabGroup> GetTabGroups(long profileId) => [];

        public static void UpdateTabGroupName(long tabGroupId, string name) { }
        public static void UpdateTabGroupSelectedTab(long tabGroupId, long selectedTabId) { }
        public static void UpdateTabGroupHasUiWindow(long tabGroupId, bool hasUiWindow) { }
        public static void UpdateTabGroupIsTabGroup(long tabGroupId, bool isTabGroup) { }
        public static void DeleteTabGroup(long tabGroupId) { }

        // ---- Tabs ----

        public static List<PersistedTab> GetTabs(long tabGroupId) => [.. _tabStore.Values
            .Where(t => t.TabGroupId == tabGroupId)
            .OrderBy(t => t.Id)];

        public static long CreateTab(long tabGroupId, string uri, string name, bool isPinned)
        {
            long id = Interlocked.Increment(ref _nextId);
            _tabStore[id] = new PersistedTab(id, tabGroupId, name, uri, isPinned, 0, false);
            return id;
        }

        public static void UpdateTabGroupId(long tabId, long tabGroupId) { }
        public static void MoveTab(long tabId, long targetWindowId, long parentTabId = 0,
            long afterTabId = 0, bool isNested = false)
        { }
        public static bool SaveTabPinState(long tabId, long windowId, bool updatePin,
            bool isPinned, bool moveToWindow, string uri, string name)
        {
            if (!_tabStore.TryGetValue(tabId, out PersistedTab? tab))
                return false;
            bool move = moveToWindow || (updatePin && !isPinned && tab.TabGroupId < 0);
            _tabStore[tabId] = tab with
            {
                TabGroupId = move ? windowId : tab.TabGroupId,
                IsPinned = updatePin ? isPinned : tab.IsPinned,
                Uri = uri,
                Name = name,
            };
            return true;
        }
        public static bool ReorderPinnedTab(long tabId, long windowId, long beforeTabId) => true;
        public static void DeleteTabs(long[] ids)
        {
            foreach (long id in ids)
            {
                _ = _tabStore.Remove(id);
            }
        }

        // ---- Favicons ----

        public record FaviconResult(byte[] Data, string? IconUrl, bool Expired);

        public static FaviconResult? GetFavicon(string pageUrl) => null;

        // ---- Recently Closed Tabs ----

        public static void CreateRecentlyClosedTab(long profileId, string name, string uri) { }
        public static List<RecentlyClosedTab> GetRecentlyClosedTabs(long profileId) => [];

        public static void DeleteRecentlyClosedTabs(long[] ids) { }

        // ---- Undo/Redo Tabs ----

        public static void CreateUndoRedoTabStep(long windowId, long[] tabIds)
        {
            List<PersistedTab> snapshots = [];
            foreach (long id in tabIds)
            {
                if (_tabStore.TryGetValue(id, out PersistedTab? tab))
                    snapshots.Add(tab);
            }

            // Trim any redo steps ahead of current position
            if (_undoStack.Count > _undoPosition)
                _undoStack.RemoveRange(_undoPosition, _undoStack.Count - _undoPosition);

            _undoStack.Add((windowId, snapshots));
            _undoPosition = _undoStack.Count;
        }

        public static void ChangeUndoRedoTabStep(long windowId, bool isUndo)
        {
            if (isUndo && _undoPosition > 0)
            {
                _undoPosition--;
                (_, List<PersistedTab> tabs) = _undoStack[_undoPosition];
                foreach (PersistedTab tab in tabs)
                    _tabStore[tab.Id] = tab;
            }
            else if (!isUndo && _undoPosition < _undoStack.Count)
            {
                (_, List<PersistedTab> tabs) = _undoStack[_undoPosition];
                foreach (PersistedTab tab in tabs)
                {
                    _ = _tabStore.Remove(tab.Id);
                }
                _undoPosition++;
            }
        }

        public static (bool UndoAvailable, bool RedoAvailable) IsUndoRedoTabStepAvailable(long windowId)
        {
            return (_undoPosition > 0, _undoPosition < _undoStack.Count);
        }

        // ---- Test helpers ----

        private static readonly Dictionary<long, PersistedTab> _tabStore = [];
        private static readonly List<(long WindowId, List<PersistedTab> Tabs)> _undoStack = [];
        private static int _undoPosition;

        public static void ResetStubState()
        {
            _nextId = 1;
            _tabStore.Clear();
            _stringSettings.Clear();
            _undoStack.Clear();
            _undoPosition = 0;
        }
    }
}
