using System;
using System.Collections.Generic;
using System.Linq;

namespace Orion
{
    public partial class MainWindowViewModel
    {
        // The core stores shared pins once per profile. Each group projects them
        // into its own live tabs so navigation can differ between workspaces.
        private sealed class SharedPinnedTabs(long profileId)
        {
            public long WindowId { get; private set; } = OrionBackend.GetSharedPinnedTabsWindowId(profileId);
            public IReadOnlyList<OrionBackend.PersistedTab> Tabs { get; private set; } = [];
            public bool AutoActivate { get; private set; }
            public event Action? Changed;

            public void Reload(bool resolveWindow = false)
            {
                if (resolveWindow)
                    WindowId = OrionBackend.GetSharedPinnedTabsWindowId(profileId);
                Tabs = WindowId == 0 ? [] : OrionBackend.GetTabs(WindowId).Where(t => t.IsPinned).ToArray();
                if (Tabs.Count > 0)
                {
                    AutoActivate = OrionBackend.GetBoolSetting(profileId,
                        "settings::tabs::auto_activate_pinned_tabs_on_loading", "settings::tabs", "auto_activate_pinned_tabs_on_loading", false);
                }
                Changed?.Invoke();
            }
        }

        private static readonly Dictionary<long, SharedPinnedTabs> s_sharedPinsByProfile = [];
        private readonly SharedPinnedTabs _sharedPins;

        private static SharedPinnedTabs GetSharedPins(long profileId)
        {
            if (!s_sharedPinsByProfile.TryGetValue(profileId, out SharedPinnedTabs? pins))
            {
                pins = new SharedPinnedTabs(profileId);
                pins.Reload();
                s_sharedPinsByProfile.Add(profileId, pins);
            }
            return pins;
        }

        private void ProjectSharedPins(TabGroup group)
        {
            if (group.IsPrivate)
                return;

            var sharedIds = new HashSet<long>(_sharedPins.Tabs.Select(t => t.Id));
            group.DismissedSharedPins.IntersectWith(sharedIds);

            bool changed = false;
            foreach (BrowserTab oldPin in group.Tabs.Where(t => t.Pin?.Scope == TabPinScope.Profile).ToArray())
            {
                if (!sharedIds.Contains(oldPin.Id))
                    changed |= group.Tabs.Remove(oldPin);
            }

            int nextIndex = 0;
            foreach (OrionBackend.PersistedTab row in _sharedPins.Tabs)
            {
                if (group.DismissedSharedPins.Contains(row.Id))
                    continue;

                BrowserTab? tab = group.Tabs.FirstOrDefault(t => t.Id == row.Id);
                if (tab == null)
                {
                    tab = RestoreTab(row, group.Id, TabPinScope.Profile);
                    tab.IsHibernated = !_sharedPins.AutoActivate;
                    group.Tabs.Insert(nextIndex, tab);
                    _ = LoadCachedFaviconAsync(tab);
                    changed = true;
                }
                else
                {
                    tab.SetPin(new PinnedPage(TabPinScope.Profile, PersistedPageUrl(row), row.Name));
                    int previousIndex = group.Tabs.IndexOf(tab);
                    if (previousIndex != nextIndex)
                    {
                        group.Tabs.Move(previousIndex, nextIndex);
                        changed = true;
                    }
                }
                nextIndex++;
            }

            if (changed)
                group.NotifyTreeChanged();
        }

        private void OnSharedPinsChanged()
        {
            foreach (TabGroupViewModel groupVm in TabGroups.ToArray())
            {
                TabGroup group = groupVm.Model;
                ProjectSharedPins(group);
                if (groupVm.ActiveTab is { Pin.Scope: TabPinScope.Profile } selected && !group.TabExists(selected))
                    groupVm.ActiveTab = group.FlattenedTabs.FirstOrDefault(t => !t.IsPinned) ?? group.Tabs.FirstOrDefault();
            }

            foreach (BrowserTab stale in _tabViewModels.Keys.Where(t => t.Pin?.Scope == TabPinScope.Profile
                && !TabGroups.Any(group => group.Model.TabExists(t))).ToArray())
            {
                DisposeTabViewModel(stale);
            }
        }

        private static string PersistedPageUrl(OrionBackend.PersistedTab row) =>
            row.Uri.Length > 0 ? row.Uri : "about:blank";

        private static BrowserTab RestoreTab(OrionBackend.PersistedTab row, long groupId,
            TabPinScope pinScope = TabPinScope.Workspace)
        {
            string url = PersistedPageUrl(row);
            var tab = new BrowserTab
            {
                Id = row.Id,
                Title = row.Name,
                Url = url,
                GroupId = groupId,
                IsHibernated = true,
            };
            if (row.IsPinned)
                tab.SetPin(new PinnedPage(pinScope, url, row.Name));
            return tab;
        }

        internal bool SetTabPinScope(BrowserTab tab, TabPinScope? scope)
        {
            if (ActiveGroup?.Model is not { } group || group.IsPrivate || tab.Id == 0
                || !group.TabExists(tab) || (scope == TabPinScope.Workspace && !group.IsSaved))
            {
                return false;
            }

            PinnedPage? previous = tab.Pin;
            if (previous?.Scope == scope)
                return true;

            long destination = scope == TabPinScope.Profile ? _sharedPins.WindowId : group.Id;
            PinnedPage? pin = scope is TabPinScope selectedScope
                ? new PinnedPage(selectedScope, previous?.Url ?? tab.Url, previous?.Title ?? tab.Title)
                : null;

            // For an unsaved group, the core itself moves a shared row back on
            // unpin. Explicitly setting update_window_id there redirects it back
            // to shared storage because the original row is pinned.
            bool move = destination != group.Id || (group.IsSaved && previous?.Scope == TabPinScope.Profile);
            if (!OrionBackend.SaveTabPinState(tab.Id, destination, updatePin: true,
                isPinned: pin != null, moveToWindow: move, pin?.Url ?? tab.Url, pin?.Title ?? tab.Title))
            {
                return false;
            }

            tab.SetPin(pin);
            if (scope != TabPinScope.Workspace)
                DetachPinFromFolder(group, tab);
            group.PositionChangedPin(tab);
            if (previous?.Scope == TabPinScope.Profile || scope == TabPinScope.Profile)
                _sharedPins.Reload();

            if (ActiveTab == tab)
                OrionBackend.UpdateTabGroupSelectedTab(group.Id, tab.Id);
            return true;
        }

        internal bool UseCurrentPageAsPin(BrowserTab tab)
        {
            if (tab.Pin is not { } pin)
                return false;

            long windowId = pin.Scope == TabPinScope.Profile ? _sharedPins.WindowId : tab.GroupId;
            if (!OrionBackend.SaveTabPinState(tab.Id, windowId, updatePin: false,
                isPinned: true, moveToWindow: false, tab.Url, tab.Title))
            {
                return false;
            }

            tab.SetPin(pin with { Url = tab.Url, Title = tab.Title });
            if (pin.Scope == TabPinScope.Profile)
                _sharedPins.Reload();
            return true;
        }

        internal void PersistTabReorder(BrowserTab tab, TabGroup group, BrowserTab? parent,
            IEnumerable<BrowserTab> following, bool isNested)
        {
            bool shared = tab.Pin?.Scope == TabPinScope.Profile;
            long windowId = shared ? _sharedPins.WindowId : group.Id;
            long nextId = following.FirstOrDefault(t => (t.Pin?.Scope == TabPinScope.Profile) == shared)?.Id ?? 0;
            if (tab.IsPinned)
                _ = OrionBackend.ReorderPinnedTab(tab.Id, windowId, nextId);
            else
                OrionBackend.MoveTab(tab.Id, windowId, parent?.Id ?? 0, nextId, isNested);
            if (shared)
                _sharedPins.Reload();
        }

        internal void StopTabGroupSync()
        {
            _sharedPins.Changed -= OnSharedPinsChanged;
            SharedSavedGroupsForProfile.CollectionChanged -= OnSharedSavedGroupsChanged;
        }
    }
}
