using System;
using System.Linq;
using Xunit;

namespace Orion
{
    [Collection("MainWindowViewModel shared state")]
    public class WorkspacePinFolderTests : IDisposable
    {
        private static Profile Profile => new() { Id = 1, Name = "Test", IsDefault = true };

        public WorkspacePinFolderTests() => Reset();

        public void Dispose() => Reset();

        private static void Reset()
        {
            MainWindowViewModel.ResetSharedState();
            OrionBackend.ResetStubState();
        }

        [Fact]
        public void MovingAndUnpackingFolders_LeavesPinsInTheWorkspace()
        {
            var vm = new MainWindowViewModel(Profile);
            TabGroup workspace = vm.CreateTabGroup(forceEmpty: true).Model;
            BrowserTab first = vm.CreateNewTab("https://example.com/first")!;
            BrowserTab second = vm.CreateNewTab("https://example.com/second")!;
            Assert.True(vm.SetTabPinScope(first, TabPinScope.Workspace));
            Assert.True(vm.SetTabPinScope(second, TabPinScope.Workspace));
            Assert.True(vm.CreatePinFolder(workspace, first, "Reference"));
            Assert.True(vm.CreatePinFolder(workspace, second, "Tools"));
            Guid target = workspace.PinFolders[1].Id;

            Assert.True(vm.MovePinToFolder(workspace, first, target));
            Assert.Empty(workspace.PinFolders[0].TabIds);
            Assert.Equal(new[] { second.Id, first.Id }, workspace.PinFolders[1].TabIds);
            Assert.True(vm.ReorderPinInFolder(workspace, target, first.Id, -1));
            Assert.Equal(new[] { first.Id, second.Id }, workspace.PinFolders[1].TabIds);

            Assert.True(vm.UnpackPinFolder(workspace, target));
            Assert.Single(workspace.PinFolders);
            Assert.All(workspace.Tabs, tab =>
            {
                Assert.Equal(TabPinScope.Workspace, tab.Pin!.Scope);
                Assert.False(tab.IsInPinFolder);
            });
            Assert.Equal(2, OrionBackend.GetTabs(workspace.Id).Count);
        }

        [Fact]
        public void RestoringFolders_RetainsOrderAndExpansionAndRepairsStaleMembers()
        {
            var vm = new MainWindowViewModel(Profile);
            TabGroup workspace = vm.CreateTabGroup(forceEmpty: true).Model;
            BrowserTab first = vm.CreateNewTab("https://example.com/first")!;
            BrowserTab stale = vm.CreateNewTab("https://example.com/stale")!;
            BrowserTab shared = vm.CreateNewTab("https://example.com/shared")!;
            Assert.True(vm.SetTabPinScope(first, TabPinScope.Workspace));
            Assert.True(vm.SetTabPinScope(stale, TabPinScope.Workspace));
            Assert.True(vm.SetTabPinScope(shared, TabPinScope.Workspace));
            Assert.True(vm.CreatePinFolder(workspace, first, "First"));
            Assert.True(vm.CreatePinFolder(workspace, stale, "Second"));
            Guid firstFolder = workspace.PinFolders[0].Id;
            Guid secondFolder = workspace.PinFolders[1].Id;
            Assert.True(vm.MovePinToFolder(workspace, shared, firstFolder));
            Assert.True(vm.SetPinFolderExpanded(workspace, firstFolder, false));
            Assert.True(vm.ReorderPinFolder(workspace, secondFolder, -1));

            Assert.True(vm.SetTabPinScope(shared, TabPinScope.Profile));
            Assert.False(shared.IsInPinFolder);
            Assert.Equal(new[] { first.Id }, workspace.PinFolders[1].TabIds);
            OrionBackend.DeleteTabs([stale.Id]);
            MainWindowViewModel.ResetSharedState();
            var reopened = new MainWindowViewModel(Profile,
                [new OrionBackend.PersistedTabGroup(workspace.Id, Profile.Id, "Workspace",
                    IsTabGroup: false, SelectedTabId: first.Id, HasUiWindow: true, IsPrivate: false)]);
            TabGroup restored = reopened.ActiveGroup!.Model;

            Assert.Equal(new[] { secondFolder, firstFolder }, restored.PinFolders.Select(f => f.Id));
            Assert.Empty(restored.PinFolders[0].TabIds);
            Assert.False(restored.PinFolders[1].IsExpanded);
            Assert.Equal(new[] { first.Id }, restored.PinFolders[1].TabIds);
            Assert.True(restored.Tabs.Single(t => t.Id == first.Id).IsInPinFolder);
            Assert.False(restored.Tabs.Single(t => t.Id == shared.Id).IsInPinFolder);
        }
    }
}
