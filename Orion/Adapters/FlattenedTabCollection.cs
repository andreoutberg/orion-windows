using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace Orion
{
    /// <summary>
    /// A projection collection that provides a flattened view of a TabGroup's tree structure.
    /// Implements IList for WinRT/TabView interop, allowing drag-drop reordering.
    /// Reads enumerate the tree on-demand; writes modify the underlying tree directly.
    /// </summary>
    public class FlattenedTabCollection(TabGroup group) : IList<BrowserTab>, IList, INotifyCollectionChanged
    {
        private readonly TabGroup _group = group ?? throw new ArgumentNullException(nameof(group));

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        /// <summary>
        /// Raised after a TabView drag-drop reorder (RemoveAt + Insert) completes.
        /// Passes the moved tab so listeners can persist only that tab's new position.
        /// </summary>
        public event Action<BrowserTab>? Reordered;

        public void Refresh()
        {
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public BrowserTab this[int index]
        {
            get => _group.FlattenedTabs.ElementAt(index);
            set => throw new NotSupportedException();
        }

        public int Count => _group.FlattenedTabs.Count();

        public bool IsReadOnly => false;

        public void Insert(int index, BrowserTab tab)
        {
            if (_group.TabExists(tab))
                _ = _group.FindContainingCollection(tab).Remove(tab);

            int pinnedCount = _group.Tabs.Count(t => t.IsPinned);
            int sharedCount = _group.Tabs.Count(t => t.Pin?.Scope == TabPinScope.Profile);
            if (tab.Pin?.Scope == TabPinScope.Profile)
                index = Math.Min(index, sharedCount);
            else if (tab.IsPinned)
                index = Math.Clamp(index, sharedCount, pinnedCount);
            else if (index < pinnedCount)
                index = pinnedCount;

            BrowserTab? target = _group.FlattenedTabs.ElementAtOrDefault(index);
            if (target != null)
            {
                Collection<BrowserTab> collection = _group.FindContainingCollection(target);
                collection.Insert(collection.IndexOf(target), tab);
            }
            else
            {
                _group.Tabs.Add(tab);
            }
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            Reordered?.Invoke(tab);
        }

        public void RemoveAt(int index)
        {
            // No-op: TabView calls RemoveAt + Insert as a pair for drag-drop reorder.
            // Insert handles removal atomically to avoid firing Reset mid-drag.
        }

        public IEnumerator<BrowserTab> GetEnumerator() => _group.FlattenedTabs.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int IndexOf(BrowserTab item) => _group.FlattenedTabs.ToList().IndexOf(item);

        public bool Contains(BrowserTab item) => _group.FlattenedTabs.Contains(item);

        public void Add(BrowserTab item) => _group.Tabs.Add(item);

        public bool Remove(BrowserTab item) => _group.FindContainingCollection(item).Remove(item);

        public void Clear() => _group.Tabs.Clear();

        public void CopyTo(BrowserTab[] array, int arrayIndex) => _group.FlattenedTabs.ToList().CopyTo(array, arrayIndex);

        // IList (non-generic) - required for WinRT interop
        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }
        bool IList.IsFixedSize => false;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        int IList.Add(object? value)
        {
            if (value is BrowserTab tab)
                Add(tab);
            return Count - 1;
        }

        bool IList.Contains(object? value) => value is BrowserTab tab && Contains(tab);

        int IList.IndexOf(object? value) => value is BrowserTab tab ? IndexOf(tab) : -1;

        void IList.Insert(int index, object? value)
        {
            if (value is BrowserTab tab)
                Insert(index, tab);
        }

        void IList.Remove(object? value)
        {
            if (value is BrowserTab tab)
                _ = Remove(tab);
        }

        void ICollection.CopyTo(Array array, int index) => _group.FlattenedTabs.ToList().CopyTo((BrowserTab[])array, index);
    }
}
