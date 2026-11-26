using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using Windows.System;
using Xunit;

namespace Orion
{
    [Collection("MainWindowViewModel shared state")]
    public class MainWindowViewModelTests : IDisposable
    {
        private static Profile TestProfile => new() { Id = 1, Name = "Test", IsDefault = true };

        public MainWindowViewModelTests()
        {
            MainWindowViewModel.ResetSharedState();
            OrionBackend.ResetStubState();
        }

        public void Dispose()
        {
            MainWindowViewModel.ResetSharedState();
            OrionBackend.ResetStubState();
        }

        [Fact]
        public void Construction_CreatesOneTabGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = Assert.Single(vm.TabGroups);
        }

        [Fact]
        public void Construction_ActiveGroupIsNotNull()
        {
            var vm = new MainWindowViewModel(TestProfile);
            Assert.NotNull(vm.ActiveGroup);
        }

        [Fact]
        public void Construction_ActiveGroupHasOneTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = Assert.Single(vm.ActiveGroup!.Model.Tabs);
        }

        [Fact]
        public void Construction_OneUnsavedTabGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            Assert.NotNull(vm.UnsavedTabGroup);
            Assert.Empty(vm.SavedTabGroups);
        }

        [Fact]
        public void WorkspaceHeaderUsesTheNameWithoutTheTabCount()
        {
            var vm = new MainWindowViewModel(TestProfile);
            Assert.Equal("This Window", vm.ActiveGroupName);

            TabGroup named = vm.CreateTabGroup(forceEmpty: true).Model;
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
            named.Name = "Research";
            _ = vm.CreateNewTab("https://example.com");

            Assert.Equal("Research", vm.ActiveGroupName);
            Assert.Contains(nameof(MainWindowViewModel.ActiveGroupName), changed);
            vm.SelectTabGroup(vm.UnsavedTabGroup!);
            Assert.Equal("This Window", vm.ActiveGroupName);
        }

        [Fact]
        public void ChangingPinScope_MovesOneRowAndUpdatesEveryWorkspace()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroup home = vm.UnsavedTabGroup!;
            TabGroup first = vm.CreateTabGroup(forceEmpty: true).Model;
            BrowserTab tab = vm.CreateNewTab("https://example.com/start", "Start")!;
            TabGroup second = vm.CreateTabGroup(forceEmpty: true).Model;
            vm.SelectTabGroup(first);

            Assert.True(vm.SetTabPinScope(tab, TabPinScope.Workspace));
            Assert.Same(tab, Assert.Single(first.Tabs));
            Assert.Empty(second.Tabs);

            Assert.True(vm.SetTabPinScope(tab, TabPinScope.Profile));
            Assert.Same(tab, Assert.Single(first.Tabs));
            BrowserTab projection = Assert.Single(second.Tabs);
            Assert.NotSame(tab, projection);
            Assert.Equal(tab.Id, projection.Id);
            Assert.Contains(home.Tabs, t => t.Id == tab.Id);
            long sharedWindow = OrionBackend.GetSharedPinnedTabsWindowId(TestProfile.Id);
            Assert.Equal(tab.Id, Assert.Single(OrionBackend.GetTabs(sharedWindow)).Id);
            Assert.Empty(OrionBackend.GetTabs(first.Id));

            vm.SelectTabGroup(second);
            Assert.True(vm.SetTabPinScope(projection, TabPinScope.Workspace));
            Assert.Empty(first.Tabs);
            Assert.NotEqual(tab, vm.TabGroups[1].ActiveTab);
            Assert.DoesNotContain(home.Tabs, t => t.Id == tab.Id);
            Assert.Empty(OrionBackend.GetTabs(sharedWindow));
            Assert.Equal(tab.Id, Assert.Single(OrionBackend.GetTabs(second.Id)).Id);
        }

        [Fact]
        public void UpdatingSavedPinPage_PreservesLiveUrlsUntilExplicitNavigation()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab tab = vm.ActiveTab!;
            tab.Url = "https://example.com/saved";
            Assert.True(vm.SetTabPinScope(tab, TabPinScope.Profile));
            TabGroup second = vm.CreateTabGroup(forceEmpty: true).Model;
            BrowserTab projection = Assert.Single(second.Tabs);

            tab.Url = "https://example.com/current";
            Assert.Equal("https://example.com/saved", tab.Pin!.Url);
            Assert.True(vm.UseCurrentPageAsPin(tab));

            Assert.Equal("https://example.com/current", projection.Pin!.Url);
            Assert.Equal("https://example.com/saved", projection.Url);
            var newWindow = new MainWindowViewModel(TestProfile);
            BrowserTab restored = Assert.Single(newWindow.UnsavedTabGroup!.Tabs, t => t.Id == tab.Id);
            Assert.Equal("https://example.com/current", restored.Url);
        }

        [Fact]
        public void UnpinFromTemporaryWindow_KeepsTheCurrentPageAndRemovesProjections()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab tab = vm.ActiveTab!;
            Assert.False(vm.SetTabPinScope(tab, TabPinScope.Workspace));
            Assert.True(vm.SetTabPinScope(tab, TabPinScope.Profile));
            TabGroup saved = vm.CreateTabGroup(forceEmpty: true).Model;
            vm.SelectTabGroup(vm.UnsavedTabGroup!);
            tab.Url = "https://example.com/current";

            Assert.True(vm.SetTabPinScope(tab, null));

            Assert.False(tab.IsPinned);
            Assert.Null(tab.Pin);
            Assert.Empty(saved.Tabs);
            Assert.Equal(tab.Url, Assert.Single(OrionBackend.GetTabs(vm.UnsavedTabGroup!.Id)).Uri);
        }

        [Fact]
        public void Construction_UnsavedTabGroupIsModel()
        {
            var vm = new MainWindowViewModel(TestProfile);
            Assert.Equal(vm.ActiveGroup!.Model, vm.UnsavedTabGroup);
        }

        [Fact]
        public void CreateTabGroup_ForceEmpty_NewGroupHasZeroTabs()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel groupVm = vm.CreateTabGroup(forceEmpty: true);
            Assert.Empty(groupVm.Model.Tabs);
        }

        [Fact]
        public void CreateTabGroup_ForceEmpty_TabGroupsCountIncreases()
        {
            var vm = new MainWindowViewModel(TestProfile);
            int before = vm.TabGroups.Count;
            _ = vm.CreateTabGroup(forceEmpty: true);
            Assert.Equal(before + 1, vm.TabGroups.Count);
        }

        [Fact]
        public void CreateTabGroup_ForceEmpty_ActiveGroupSwitches()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel? original = vm.ActiveGroup;
            TabGroupViewModel newGroupVm = vm.CreateTabGroup(forceEmpty: true);
            Assert.Equal(newGroupVm, vm.ActiveGroup);
            Assert.NotEqual(original, vm.ActiveGroup);
        }

        [Fact]
        public void CreateTabGroup_NotForceEmpty_NewGroupHasOneTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel groupVm = vm.CreateTabGroup(forceEmpty: false);
            _ = Assert.Single(groupVm.Model.Tabs);
        }

        [Fact]
        public void CreateTabGroup_StartsInSavedGroups()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel groupVm = vm.CreateTabGroup(forceEmpty: true);
            Assert.Contains(groupVm.Model, vm.SavedTabGroups);
            Assert.NotEqual(groupVm.Model, vm.UnsavedTabGroup);
        }

        [Fact]
        public void CreateNewTab_ReturnsTabAddedToActiveGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            int before = vm.ActiveGroup!.Model.Tabs.Count;
            BrowserTab? tab = vm.CreateNewTab();
            Assert.NotNull(tab);
            Assert.Equal(before + 1, vm.ActiveGroup.Model.Tabs.Count);
            Assert.Contains(tab, vm.ActiveGroup.Model.Tabs);
        }

        [Fact]
        public void CreateNewTab_SetsActiveTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? tab = vm.CreateNewTab();
            Assert.Equal(tab, vm.ActiveGroup!.ActiveTab);
        }

        [Fact]
        public void CloseTab_ReturnsFalseAndDecreasesCount_WhenOtherTabsRemain()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://example.com");
            BrowserTab tab = vm.ActiveGroup!.Model.Tabs[0];
            int before = vm.ActiveGroup.Model.Tabs.Count;

            bool shouldClose = vm.CloseTab(tab);

            Assert.False(shouldClose);
            Assert.Equal(before - 1, vm.ActiveGroup.Model.Tabs.Count);
        }

        [Fact]
        public void CloseTab_ReturnsTrue_WhenLastTabInLastGroupClosed()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab lastTab = vm.ActiveGroup!.Model.Tabs[0];

            bool shouldClose = vm.CloseTab(lastTab);

            Assert.True(shouldClose);
        }

        [Fact]
        public void CloseTab_ReturnsTrue_WhenOnlySavedGroupsRemain()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateTabGroup(forceEmpty: true); // adds a saved group
            vm.SelectTabGroup(vm.TabGroups[0].Model); // select the unsaved group
            BrowserTab lastTab = vm.ActiveGroup!.Model.Tabs[0];

            bool shouldClose = vm.CloseTab(lastTab);

            Assert.True(shouldClose);
        }

        [Fact]
        public void SwitchToTab_ChangesActiveTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab target = vm.ActiveGroup!.Model.Tabs[0];
            vm.SwitchToTab(target);
            Assert.Equal(target, vm.ActiveGroup.ActiveTab);
        }

        [Fact]
        public void SwitchToTab_DoesNothing_WhenTabNotInActiveGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? before = vm.ActiveGroup!.ActiveTab;
            var foreignTab = new BrowserTab { Id = 9999 };

            vm.SwitchToTab(foreignTab);

            Assert.Equal(before, vm.ActiveGroup.ActiveTab);
        }

        [Fact]
        public void SelectTabGroup_ChangesActiveGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateTabGroup(forceEmpty: true);
            TabGroupViewModel vm1 = vm.TabGroups[0];

            vm.SelectTabGroup(vm1.Model);

            Assert.Equal(vm1, vm.ActiveGroup);
        }

        [Fact]
        public void SelectTabGroup_OldGroupIsSelectedFalse_NewGroupIsSelectedTrue()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel vm1 = vm.TabGroups[0];
            TabGroupViewModel vm2 = vm.CreateTabGroup(forceEmpty: true);

            vm.SelectTabGroup(vm1.Model);

            Assert.True(vm1.IsSelected);
            Assert.False(vm2.IsSelected);
        }

        [Fact]
        public void SelectTabGroup_FiresActiveTabPropertyChanged()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel vm1 = vm.TabGroups[0];
            TabGroupViewModel vm2 = vm.CreateTabGroup(forceEmpty: true);
            vm2.Model.AddTab(new BrowserTab { Id = 50 });
            vm2.ActiveTab = vm2.Model.Tabs[0];

            // CreateTabGroup already made vm2 active; switch away first so we can switch back
            vm.SelectTabGroup(vm1.Model);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            vm.SelectTabGroup(vm2.Model);

            Assert.Contains(nameof(MainWindowViewModel.ActiveTab), raised);
        }

        [Fact]
        public void SelectTabGroupById_ChangesActiveGroupById()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel vm2 = vm.CreateTabGroup(forceEmpty: true);

            vm.SelectTabGroupById(vm2.Model.Id);

            Assert.Equal(vm2, vm.ActiveGroup);
        }

        [Fact]
        public void DeleteTabGroup_RemovesGroupFromTabGroups()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel vm2 = vm.CreateTabGroup(forceEmpty: true);

            bool result = vm.DeleteTabGroup(vm2.Model);

            Assert.True(result);
            Assert.DoesNotContain(vm2, vm.TabGroups);
        }

        [Fact]
        public void DeleteTabGroup_ReturnsFalse_WhenGroupNotFound()
        {
            var vm = new MainWindowViewModel(TestProfile);
            var foreign = new TabGroup { Id = 9999 };

            bool result = vm.DeleteTabGroup(foreign);

            Assert.False(result);
        }

        [Fact]
        public void DeleteTabGroup_ActiveGroup_SwitchesToFirstRemaining()
        {
            var vm = new MainWindowViewModel(TestProfile);
            TabGroupViewModel vm1 = vm.TabGroups[0];
            TabGroupViewModel vm2 = vm.CreateTabGroup(forceEmpty: true);

            vm.SelectTabGroup(vm2.Model);
            _ = vm.DeleteTabGroup(vm2.Model);

            Assert.Equal(vm1, vm.ActiveGroup);
        }

        [Fact]
        public void SaveUnsavedGroup_CreatesNewSavedGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            int before = vm.TabGroups.Count;

            TabGroupViewModel saved = vm.SaveUnsavedGroup("My Group");

            Assert.Equal(before + 1, vm.TabGroups.Count);
            Assert.True(saved.Model.IsSaved);
            Assert.Equal("My Group", saved.Model.Name);
            Assert.Contains(saved.Model, vm.SavedTabGroups);
        }

        [Fact]
        public void SaveUnsavedGroup_MovesTabsToSavedGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://a.com");
            _ = vm.CreateNewTab("https://b.com");
            BrowserTab[] originalTabs = [.. vm.UnsavedTabGroup!.FlattenedTabs];

            TabGroupViewModel saved = vm.SaveUnsavedGroup("My Group");

            // All original tabs should be in the saved group
            foreach (BrowserTab tab in originalTabs)
                Assert.True(saved.Model.TabExists(tab));
        }

        [Fact]
        public void SaveUnsavedGroup_UnsavedGroupPersistsWithFreshTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://a.com");

            _ = vm.SaveUnsavedGroup("My Group");

            Assert.NotNull(vm.UnsavedTabGroup);
            Assert.False(vm.UnsavedTabGroup.IsSaved);
            _ = Assert.Single(vm.UnsavedTabGroup.Tabs);
        }

        [Fact]
        public void SaveUnsavedGroup_SwitchesToSavedGroup()
        {
            var vm = new MainWindowViewModel(TestProfile);

            TabGroupViewModel saved = vm.SaveUnsavedGroup("My Group");

            Assert.Equal(saved, vm.ActiveGroup);
        }

        [Fact]
        public void SaveUnsavedGroup_PreservesActiveTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://a.com");
            BrowserTab? activeTab = vm.ActiveGroup!.ActiveTab;

            TabGroupViewModel saved = vm.SaveUnsavedGroup("My Group");

            Assert.Equal(activeTab, saved.ActiveTab);
        }

        [Fact]
        public void HorizontalTabsVisibility_HiddenInVerticalLayout()
        {
            var vm = new MainWindowViewModel(TestProfile) { TabLayout = TabLayout.vertical };
            Assert.Equal(Visibility.Collapsed, vm.HorizontalTabsVisibility);
        }

        [Fact]
        public void HorizontalTabsVisibility_VisibleInStandardLayout()
        {
            var vm = new MainWindowViewModel(TestProfile) { TabLayout = TabLayout.standard };
            Assert.Equal(Visibility.Visible, vm.HorizontalTabsVisibility);
        }

        [Fact]
        public void HorizontalTabsVisibility_CollapsedInCompactLayout()
        {
            var vm = new MainWindowViewModel(TestProfile) { TabLayout = TabLayout.compact };
            Assert.Equal(Visibility.Collapsed, vm.HorizontalTabsVisibility);
            Assert.Equal(Visibility.Visible, vm.CompactTabsVisibility);
        }

        [Fact]
        public void SidebarTabsVisibility_IndependentOfTabLayout()
        {
            var vm = new MainWindowViewModel(TestProfile)
            {
                TabLayout = TabLayout.standard,
                SelectedSidebarPage = SidebarPage.tabs
            };
            Assert.Equal(Visibility.Visible, vm.SidebarTabsModeVisibility);
            Assert.Equal(Visibility.Visible, vm.VerticalGroupHeadersVisibility);
            Assert.Equal(Visibility.Visible, vm.VerticalTabsVisibility);
        }

        [Fact]
        public void SidebarBookmarksVisibility_VisibleWhenBookmarksMode()
        {
            var vm = new MainWindowViewModel(TestProfile) { SelectedSidebarPage = SidebarPage.bookmarks };
            Assert.Equal(Visibility.Visible, vm.SidebarBookmarksVisibility);
            Assert.Equal(Visibility.Collapsed, vm.SidebarTabsModeVisibility);
            Assert.Equal(Visibility.Collapsed, vm.SidebarTabGroupsVisibility);
            Assert.Equal(Visibility.Collapsed, vm.SidebarReadingListVisibility);
        }

        [Fact]
        public void DuplicateTab_CreatesNewTabWithSameUrlAndTitle()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab original = vm.ActiveGroup!.Model.Tabs[0];
            original.Url = "https://example.com";
            original.Title = "Example";

            BrowserTab? dup = vm.DuplicateTab(original);

            Assert.NotNull(dup);
            Assert.NotEqual(original, dup);
            Assert.Equal("https://example.com", dup.Url);
            Assert.Equal("Example", dup.Title);
        }

        [Fact]
        public void CloseOtherTabs_KeepsOnlySpecifiedTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://a.com");
            _ = vm.CreateNewTab("https://b.com");
            BrowserTab keeper = vm.ActiveGroup!.Model.Tabs[1];

            vm.CloseOtherTabs(keeper);

            _ = Assert.Single(vm.ActiveGroup.Model.Tabs);
            Assert.Equal(keeper, vm.ActiveGroup.Model.Tabs[0]);
            Assert.Equal(keeper, vm.ActiveGroup.ActiveTab);
        }

        [Fact]
        public void CloseTabsToTheRight_ClosesOnlyTabsAfterSpecified()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://a.com");
            _ = vm.CreateNewTab("https://b.com");
            BrowserTab tab0 = vm.ActiveGroup!.Model.Tabs[0];

            vm.CloseTabsToTheRight(tab0);

            _ = Assert.Single(vm.ActiveGroup.Model.Tabs);
            Assert.Equal(tab0, vm.ActiveGroup.Model.Tabs[0]);
        }

        [Fact]
        public void CloseTabsToTheRight_LastTab_DoesNothing()
        {
            var vm = new MainWindowViewModel(TestProfile);
            int count = vm.ActiveGroup!.Model.Tabs.Count;
            BrowserTab lastTab = vm.ActiveGroup.Model.Tabs[^1];

            vm.CloseTabsToTheRight(lastTab);

            Assert.Equal(count, vm.ActiveGroup.Model.Tabs.Count);
        }

        [Fact]
        public void UndoCloseTab_RestoresClosedTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? added = vm.CreateNewTab("https://example.com", "Example");
            Assert.NotNull(added);
            int countBefore = vm.ActiveGroup!.Model.Tabs.Count;

            _ = vm.CloseTab(added);
            Assert.Equal(countBefore - 1, vm.ActiveGroup.Model.Tabs.Count);

            BrowserTab? restored = vm.UndoCloseTab();
            Assert.NotNull(restored);
            Assert.Equal(countBefore, vm.ActiveGroup.Model.Tabs.Count);
            Assert.Equal("https://example.com", restored.Url);
        }

        [Fact]
        public void UndoCloseTab_SetsRestoredTabAsActive()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? added = vm.CreateNewTab("https://example.com", "Example");
            Assert.NotNull(added);

            _ = vm.CloseTab(added);
            BrowserTab? restored = vm.UndoCloseTab();

            Assert.NotNull(restored);
            Assert.Equal(restored, vm.ActiveGroup!.ActiveTab);
        }

        [Fact]
        public void UndoCloseTab_FallsBackToReopenClosedTab_WhenNoUndoSteps()
        {
            var vm = new MainWindowViewModel(TestProfile);

            // No undo steps exist, and no recently closed tabs either
            BrowserTab? result = vm.UndoCloseTab();

            Assert.Null(result);
        }

        [Fact]
        public void RedoCloseTab_RemovesRestoredTab()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? added = vm.CreateNewTab("https://example.com", "Example");
            Assert.NotNull(added);
            int countBefore = vm.ActiveGroup!.Model.Tabs.Count;

            _ = vm.CloseTab(added);
            _ = vm.UndoCloseTab();
            Assert.Equal(countBefore, vm.ActiveGroup.Model.Tabs.Count);

            vm.RedoCloseTab();
            Assert.Equal(countBefore - 1, vm.ActiveGroup.Model.Tabs.Count);
        }

        [Fact]
        public void RedoCloseTab_DoesNothing_WhenNoRedoSteps()
        {
            var vm = new MainWindowViewModel(TestProfile);
            int count = vm.ActiveGroup!.Model.Tabs.Count;

            vm.RedoCloseTab();

            Assert.Equal(count, vm.ActiveGroup.Model.Tabs.Count);
        }

        [Fact]
        public void UndoCloseTab_MultipleUndos_RestoresInOrder()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? tab1 = vm.CreateNewTab("https://a.com", "A");
            BrowserTab? tab2 = vm.CreateNewTab("https://b.com", "B");
            Assert.NotNull(tab1);
            Assert.NotNull(tab2);

            _ = vm.CloseTab(tab2);
            _ = vm.CloseTab(tab1);

            BrowserTab? restored1 = vm.UndoCloseTab();
            Assert.NotNull(restored1);
            Assert.Equal("https://a.com", restored1.Url);

            BrowserTab? restored2 = vm.UndoCloseTab();
            Assert.NotNull(restored2);
            Assert.Equal("https://b.com", restored2.Url);
        }

        [Fact]
        public void UndoRedo_ClearsRedoOnNewClose()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? tab1 = vm.CreateNewTab("https://a.com", "A");
            Assert.NotNull(tab1);

            _ = vm.CloseTab(tab1);
            _ = vm.UndoCloseTab();

            // New close should clear the redo stack
            BrowserTab? tab2 = vm.CreateNewTab("https://b.com", "B");
            Assert.NotNull(tab2);
            _ = vm.CloseTab(tab2);

            // Redo should do nothing (the old redo was cleared)
            int count = vm.ActiveGroup!.Model.Tabs.Count;
            vm.RedoCloseTab();
            Assert.Equal(count, vm.ActiveGroup.Model.Tabs.Count);
        }

        private static BrowserTab MakeChildTab(TabGroup group, BrowserTab parent, string url, string title)
        {
            long id = OrionBackend.CreateTab(group.Id, url, title, isPinned: false);
            BrowserTab child = new() { Id = id, Url = url, Title = title, GroupId = group.Id };
            parent.Children.Add(child);
            group.NotifyTreeChanged();
            return child;
        }

        [Fact]
        public void CloseTab_DefaultBehavior_PromotesChildren()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? parent = vm.CreateNewTab("https://parent.com", "Parent");
            Assert.NotNull(parent);
            TabGroup group = vm.ActiveGroup!.Model;
            BrowserTab child1 = MakeChildTab(group, parent, "https://child1.com", "Child1");
            BrowserTab child2 = MakeChildTab(group, parent, "https://child2.com", "Child2");

            _ = vm.CloseTab(parent);

            Assert.Contains(child1, group.FlattenedTabs);
            Assert.Contains(child2, group.FlattenedTabs);
            Assert.DoesNotContain(parent, group.FlattenedTabs);
        }

        [Fact]
        public void CloseTab_PromoteBehavior_PromotesChildren()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? parent = vm.CreateNewTab("https://parent.com", "Parent");
            Assert.NotNull(parent);
            TabGroup group = vm.ActiveGroup!.Model;
            BrowserTab child1 = MakeChildTab(group, parent, "https://child1.com", "Child1");
            BrowserTab child2 = MakeChildTab(group, parent, "https://child2.com", "Child2");

            _ = vm.CloseTab(parent);

            Assert.Contains(child1, group.FlattenedTabs);
            Assert.Contains(child2, group.FlattenedTabs);
            Assert.DoesNotContain(parent, group.FlattenedTabs);
        }

        [Fact]
        public void CloseTab_PreserveTree_NavigatesToStartPage()
        {
            OrionBackend.IntSettingOverrides.Value = new()
            {
                ["settings::tabs::closing_expanded_tab_behavior"] = 2,
            };

            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? parent = vm.CreateNewTab("https://parent.com", "Parent");
            Assert.NotNull(parent);
            TabGroup group = vm.ActiveGroup!.Model;
            BrowserTab child1 = MakeChildTab(group, parent, "https://child1.com", "Child1");

            _ = vm.CloseTab(parent, out BrowserTab? navigateTab);

            Assert.Same(parent, navigateTab);
            Assert.Contains(parent, group.FlattenedTabs);
            Assert.Contains(child1, group.FlattenedTabs);
        }

        [Fact]
        public void CloseTab_PreserveTree_SkipsWhenAlreadyStartPage()
        {
            OrionBackend.IntSettingOverrides.Value = new()
            {
                ["settings::tabs::closing_expanded_tab_behavior"] = 2,
            };

            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? parent = vm.CreateNewTab(BrowserTab.StartPageUrl, "Start Page");
            Assert.NotNull(parent);
            TabGroup group = vm.ActiveGroup!.Model;
            _ = MakeChildTab(group, parent, "https://child.com", "Child");

            _ = vm.CloseTab(parent, out BrowserTab? navigateTab);

            Assert.Null(navigateTab);
            Assert.DoesNotContain(parent, group.FlattenedTabs);
        }

        [Fact]
        public void CloseTab_ConvertFirstChild_PromotesFirstChildAsParent()
        {
            OrionBackend.IntSettingOverrides.Value = new()
            {
                ["settings::tabs::closing_expanded_tab_behavior"] = 1,
            };

            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? parent = vm.CreateNewTab("https://parent.com", "Parent");
            Assert.NotNull(parent);
            TabGroup group = vm.ActiveGroup!.Model;
            BrowserTab child1 = MakeChildTab(group, parent, "https://child1.com", "Child1");
            BrowserTab child2 = MakeChildTab(group, parent, "https://child2.com", "Child2");
            BrowserTab child3 = MakeChildTab(group, parent, "https://child3.com", "Child3");

            _ = vm.CloseTab(parent);

            Assert.DoesNotContain(parent, group.FlattenedTabs);
            Assert.Contains(child1, group.Tabs);
            Assert.Contains(child2, child1.Children);
            Assert.Contains(child3, child1.Children);
        }

        [Fact]
        public void CloseTab_ReturnsToLastActiveTab_ByDefault()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab first = vm.ActiveGroup!.Model.Tabs[0];
            BrowserTab? second = vm.CreateNewTab("https://second.com");
            BrowserTab? third = vm.CreateNewTab("https://third.com");
            Assert.NotNull(second);
            Assert.NotNull(third);
            vm.SwitchToTab(first);

            _ = vm.CloseTab(first);

            Assert.Equal(third, vm.ActiveTab);
        }

        [Fact]
        public void CloseTab_ActivatesNextTab_WhenReturnToLastActiveDisabled()
        {
            OrionBackend.BoolSettingOverrides.Value = new()
            {
                ["settings::tabs::return_to_last_active_tab_when_closing_current_tab"] = false,
            };

            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab first = vm.ActiveGroup!.Model.Tabs[0];
            BrowserTab? second = vm.CreateNewTab("https://second.com");
            BrowserTab? third = vm.CreateNewTab("https://third.com");
            Assert.NotNull(second);
            Assert.NotNull(third);
            vm.SwitchToTab(first);

            _ = vm.CloseTab(first);

            Assert.Equal(second, vm.ActiveTab);
        }

        [Fact]
        public void ShouldOpenBookmarkInNewTab_False_ByDefault()
        {
            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://example.com");

            Assert.False(vm.ShouldOpenBookmarkInNewTab());
        }

        [Fact]
        public void ShouldOpenBookmarkInNewTab_True_WhenEnabledOnWebsiteTab()
        {
            OrionBackend.BoolSettingOverrides.Value = new()
            {
                ["settings::browsing::open_bookmarks_in_new_tabs"] = true,
            };

            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("https://example.com");

            Assert.True(vm.ShouldOpenBookmarkInNewTab());
        }

        [Theory]
        [InlineData(VirtualKeyModifiers.None, null)]
        [InlineData(VirtualKeyModifiers.Control, OrionBackend.OpenDisposition.NewBackgroundTab)]
        [InlineData(VirtualKeyModifiers.Shift, OrionBackend.OpenDisposition.NewWindow)]
        [InlineData(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            OrionBackend.OpenDisposition.NewForegroundTab)]
        public void BookmarkGestureDisposition_MapsModifiers(
            VirtualKeyModifiers modifiers, OrionBackend.OpenDisposition? expected)
        {
            Assert.Equal(expected, MainWindowViewModel.BookmarkGestureDisposition(modifiers));
        }

        [Fact]
        public void ShouldOpenBookmarkInNewTab_True_WhenActiveTabPinned()
        {
            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? tab = vm.CreateNewTab("https://example.com");
            Assert.NotNull(tab);
            tab.IsPinned = true;

            Assert.True(vm.ShouldOpenBookmarkInNewTab());
        }

        [Fact]
        public void ShouldOpenBookmarkInNewTab_True_WhenPinnedTabOnStartPage()
        {
            OrionBackend.BoolSettingOverrides.Value = new()
            {
                ["settings::browsing::open_bookmarks_in_new_tabs"] = true,
            };

            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? tab = vm.CreateNewTab(BrowserTab.StartPageUrl);
            Assert.NotNull(tab);
            tab.IsPinned = true;

            Assert.True(vm.ShouldOpenBookmarkInNewTab());
        }

        [Fact]
        public void ShouldOpenBookmarkInNewTab_False_WhenEnabledOnBlankTab()
        {
            OrionBackend.BoolSettingOverrides.Value = new()
            {
                ["settings::browsing::open_bookmarks_in_new_tabs"] = true,
            };

            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab("about:blank");

            Assert.False(vm.ShouldOpenBookmarkInNewTab());
        }

        [Fact]
        public void ShouldOpenBookmarkInNewTab_False_WhenEnabledOnStartPage()
        {
            OrionBackend.BoolSettingOverrides.Value = new()
            {
                ["settings::browsing::open_bookmarks_in_new_tabs"] = true,
            };

            var vm = new MainWindowViewModel(TestProfile);
            _ = vm.CreateNewTab(BrowserTab.StartPageUrl);

            Assert.False(vm.ShouldOpenBookmarkInNewTab());
        }

        [Fact]
        public void CloseTab_NoChildren_IgnoresClosingBehaviorSetting()
        {
            OrionBackend.IntSettingOverrides.Value = new()
            {
                ["settings::tabs::closing_expanded_tab_behavior"] = 1,
            };

            var vm = new MainWindowViewModel(TestProfile);
            BrowserTab? tab = vm.CreateNewTab("https://solo.com", "Solo");
            Assert.NotNull(tab);
            int countBefore = vm.ActiveGroup!.Model.Tabs.Count;

            _ = vm.CloseTab(tab);

            Assert.Equal(countBefore - 1, vm.ActiveGroup.Model.Tabs.Count);
        }
    }
}
