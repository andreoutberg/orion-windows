using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace Orion
{
    /// <summary>
    /// ViewModel wrapper for TabGroup, adding UI-only state like selection and active tab.
    /// </summary>
    public partial class TabGroupViewModel : ObservableObject
    {
        public TabGroup Model { get; }

        /// <summary>
        /// Whether this group is the active/selected group.
        /// Used by the horizontal tab group header (ItemsControl) for selection highlighting.
        /// Managed by MainWindowViewModel.ActiveGroup setter.
        /// </summary>
        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        /// <summary>
        /// The currently active/displayed tab in this group.
        /// </summary>
        [ObservableProperty]
        public partial BrowserTab? ActiveTab { get; set; }

        /// <summary>
        /// The tab that was active before the current one. Backs
        /// settings::tabs::return_to_last_active_tab_when_closing_current_tab.
        /// </summary>
        private BrowserTab? _previouslyActiveTab;

        partial void OnActiveTabChanged(BrowserTab? oldValue, BrowserTab? newValue) =>
            _previouslyActiveTab = oldValue;

        public string DisplayName => Model.DisplayName;
        public string WorkspaceName => Model.WorkspaceName;

        public TabGroupViewModel(TabGroup model)
        {
            Model = model;
            Model.PropertyChanged += OnModelPropertyChanged;
        }

        private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabGroup.DisplayName))
                OnPropertyChanged(nameof(DisplayName));
            else if (e.PropertyName == nameof(TabGroup.WorkspaceName))
                OnPropertyChanged(nameof(WorkspaceName));
        }

        /// <summary>
        /// Removes a tab, switching active tab after removal so the collection
        /// is stable when OnActiveTabChanged fires (avoids TreeView crash).
        /// With returnToLastActive the previously active tab is activated;
        /// otherwise (or when it no longer exists) the next tab in display
        /// order, falling back to the previous one at the end of the strip.
        /// </summary>
        public void RemoveTab(BrowserTab tab, bool returnToLastActive)
        {
            BrowserTab? newActive = null;
            if (tab == ActiveTab)
            {
                // _previouslyActiveTab can be gone: closed in the background,
                // by a bulk close, or moved to another group.
                if (returnToLastActive && _previouslyActiveTab is { } previous && Model.TabExists(previous))
                    newActive = previous;

                if (newActive == null)
                {
                    FlattenedTabCollection tabs = Model.FlattenedTabCollection;
                    int i = tabs.IndexOf(tab);
                    int count = tabs.Count;
                    int newIndex = i + 1 < count ? i + 1 : i - 1;
                    if (newIndex >= 0)
                        newActive = tabs[newIndex];
                }
            }

            Model.RemoveTab(tab);

            if (newActive != null)
                ActiveTab = newActive;
        }
    }
}
