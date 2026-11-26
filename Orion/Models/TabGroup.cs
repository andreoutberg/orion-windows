using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace Orion
{
    /// <summary>
    /// Represents a group of browser tabs with a name, color, and collapse state.
    /// </summary>
    public partial class TabGroup : ObservableObject
    {
        /// <summary>
        /// Database ID from the core module (maps to WindowId in the DB).
        /// </summary>
        [ObservableProperty]
        public partial long Id { get; set; }

        internal bool IsPrivate { get; init; }

        internal HashSet<long> DismissedSharedPins { get; } = [];

        internal IReadOnlyList<WorkspacePinFolder> PinFolders { get; private set; } = [];

        internal event Action<bool>? PinFoldersChanged;

        internal void ApplyPinFolders(IReadOnlyList<WorkspacePinFolder> folders, bool structureChanged)
        {
            PinFolders = folders;
            var folderedPins = new HashSet<long>(folders.SelectMany(folder => folder.TabIds));
            foreach (BrowserTab tab in Tabs)
                tab.IsInPinFolder = tab.Pin?.Scope == TabPinScope.Workspace && folderedPins.Contains(tab.Id);
            PinFoldersChanged?.Invoke(structureChanged);
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        public partial bool IsSaved { get; set; }

        [ObservableProperty]
        public partial int Order { get; set; }

        /// <summary>
        /// GTK accelerator name for switching to this group (see KeyCombo);
        /// "" = unbound. Mirrors windows.keyboard_shortcut in the core DB.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KeyboardShortcutDisplay))]
        public partial string KeyboardShortcut { get; set; } = string.Empty;

        /// <summary>
        /// Windows-style display of the shortcut ("Ctrl+Alt+1"); "" when
        /// unbound or stored in a form this port cannot parse.
        /// </summary>
        public string KeyboardShortcutDisplay =>
            KeyCombo.TryParseGtkAccel(KeyboardShortcut, out KeyCombo combo)
                ? combo.ToDisplayString() : string.Empty;

        public TabGroup()
        {
            Tabs.CollectionChanged += OnTabsCollectionChanged;
            FlattenedTabCollection = new FlattenedTabCollection(this);
        }

        /// <summary>
        /// Display name: saved groups show name + tab count; unsaved show tab count only.
        /// Counts all tabs including nested children.
        /// </summary>
        public string DisplayName
        {
            get
            {
                int count = FlattenedTabs.Count();
                return IsSaved
                    ? $"{Name} ({count})"
                    : $"{count} {(count == 1 ? "tab" : "tabs")}";
            }
        }

        /// <summary>
        /// Observable collection of tabs in this group.
        /// </summary>
        public ObservableCollection<BrowserTab> Tabs { get; } = [];

        /// <summary>
        /// Flattened collection of all tabs including children (depth-first order).
        /// </summary>
        public IEnumerable<BrowserTab> FlattenedTabs => FlattenTabs(Tabs);

        /// <summary>
        /// Flattened collection as IList for TabView binding with drag-drop support.
        /// TabView requires IList to perform drag-drop reordering — it calls
        /// RemoveAt/Insert directly on the bound collection.
        /// </summary>
        public FlattenedTabCollection FlattenedTabCollection { get; }

        private static IEnumerable<BrowserTab> FlattenTabs(IEnumerable<BrowserTab> tabs)
        {
            foreach (BrowserTab tab in tabs)
            {
                yield return tab;
                foreach (BrowserTab child in FlattenTabs(tab.Children))
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// Finds the collection that contains the specified tab.
        /// </summary>
        public ObservableCollection<BrowserTab> FindContainingCollection(BrowserTab tab)
        {
            if (Tabs.Contains(tab))
                return Tabs;

            foreach (BrowserTab parent in FlattenedTabs)
            {
                if (parent.Children.Contains(tab))
                    return parent.Children;
            }

            throw new ArgumentException("Tab not found in tree", nameof(tab));
        }

        /// <summary>
        /// Returns the parent tab, or null if the tab is at the root level.
        /// </summary>
        public BrowserTab? FindParentTab(BrowserTab tab)
        {
            if (Tabs.Contains(tab))
                return null;

            foreach (BrowserTab parent in FlattenedTabs)
            {
                if (parent.Children.Contains(tab))
                    return parent;
            }

            throw new ArgumentException("Tab not found in tree", nameof(tab));
        }

        public void AddTab(BrowserTab tab)
        {
            Tabs.Add(tab);
            NotifyTreeChanged();
        }

        public void InsertTabAfter(BrowserTab newTab, BrowserTab referenceTab)
        {
            ObservableCollection<BrowserTab> collection;
            try { collection = FindContainingCollection(referenceTab); }
            catch (ArgumentException) { collection = Tabs; }
            int index = collection.IndexOf(referenceTab);
            collection.Insert(index + 1, newTab);
            NotifyTreeChanged();
        }

        /// <summary>
        /// Removes a tab from the tree, re-parenting its children to the parent collection.
        /// Does not handle active tab switching — callers should use
        /// TabGroupViewModel.RemoveTab() for that.
        /// </summary>
        public void RemoveTab(BrowserTab tab)
        {
            ObservableCollection<BrowserTab> containingCollection;
            try
            {
                containingCollection = FindContainingCollection(tab);
            }
            catch (ArgumentException)
            {
                return; // Tab not found
            }

            _ = containingCollection.Remove(tab);
            foreach (BrowserTab child in tab.Children)
            {
                containingCollection.Add(child);
            }
            NotifyTreeChanged();
        }

        /// <summary>
        /// Removes a tab, promoting its first child to take its place.
        /// Remaining children become children of the promoted first child.
        /// </summary>
        public void RemoveTabConvertFirstChild(BrowserTab tab)
        {
            ObservableCollection<BrowserTab> containingCollection;
            try
            {
                containingCollection = FindContainingCollection(tab);
            }
            catch (ArgumentException)
            {
                return;
            }

            int index = containingCollection.IndexOf(tab);
            _ = containingCollection.Remove(tab);

            if (tab.Children.Count > 0)
            {
                BrowserTab firstChild = tab.Children[0];
                containingCollection.Insert(index, firstChild);
                for (int i = 1; i < tab.Children.Count; i++)
                    firstChild.Children.Add(tab.Children[i]);
            }

            NotifyTreeChanged();
        }

        /// <summary>
        /// Ensures pinned tabs are at the root level, at the top, and have no children.
        /// Called after TreeView drag-drop which modifies collections directly.
        /// </summary>
        public void EnforcePinnedConstraints()
        {
            // Un-nest any pinned tab that was dropped as a child
            foreach (BrowserTab parent in FlattenedTabs.ToList())
            {
                for (int i = parent.Children.Count - 1; i >= 0; i--)
                {
                    if (parent.Children[i].IsPinned)
                    {
                        BrowserTab pinned = parent.Children[i];
                        parent.Children.RemoveAt(i);
                        Tabs.Insert(0, pinned);
                    }
                }
            }

            // Move children off pinned tabs (promote to root after pinned section)
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].IsPinned && Tabs[i].Children.Count > 0)
                {
                    BrowserTab pinned = Tabs[i];
                    int insertAt = i + 1;
                    while (pinned.Children.Count > 0)
                    {
                        BrowserTab child = pinned.Children[0];
                        pinned.Children.RemoveAt(0);
                        Tabs.Insert(insertAt++, child);
                    }
                }
            }

            // Keep the profile pin section before the workspace pin section.
            BrowserTab[] pins = Tabs.Where(t => t.IsPinned)
                .OrderByDescending(t => t.Pin?.Scope == TabPinScope.Profile).ToArray();
            for (int i = 0; i < pins.Length; i++)
            {
                int previousIndex = Tabs.IndexOf(pins[i]);
                if (previousIndex != i)
                    Tabs.Move(previousIndex, i);
            }
        }

        internal void PositionChangedPin(BrowserTab tab)
        {
            ObservableCollection<BrowserTab> previous = FindContainingCollection(tab);
            int previousIndex = previous.IndexOf(tab);
            previous.RemoveAt(previousIndex);
            foreach (BrowserTab child in tab.Children)
                previous.Insert(previousIndex++, child);
            tab.Children.Clear();

            int insertAt = tab.Pin?.Scope == TabPinScope.Profile
                ? Tabs.TakeWhile(t => t.Pin?.Scope == TabPinScope.Profile).Count()
                : Tabs.TakeWhile(t => t.IsPinned).Count();
            Tabs.Insert(insertAt, tab);
            NotifyTreeChanged();
        }

        public bool TabExists(BrowserTab tab)
        {
            foreach (BrowserTab t in FlattenedTabs)
            {
                if (t == tab)
                    return true;
            }

            return false;
        }

        private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(DisplayName));

        /// <summary>
        /// Refreshes FlattenedTabCollection after the tab tree structure has changed.
        /// </summary>
        public void NotifyTreeChanged()
        {
            FlattenedTabCollection.Refresh();
            OnPropertyChanged(nameof(DisplayName));
        }
    }
}
