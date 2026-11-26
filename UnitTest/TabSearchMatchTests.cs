using System.Linq;
using Xunit;

namespace Orion
{
    public class TabSearchMatchTests
    {
        [Fact]
        public void WorkspaceNameIsAnActionEvenWhenTheWorkspaceIsEmpty()
        {
            var work = new TabGroup { IsSaved = true, Name = "Work" };
            var personal = new TabGroup { IsSaved = true, Name = "Personal" };

            TabSearchMatch result = Assert.Single(TabSearchMatch.Find(new[] { work, personal }, personal, "  WORK  "));

            Assert.Same(work, result.Group);
            Assert.Null(result.Tab);
            Assert.Equal("Workspace", result.Context);
        }

        [Fact]
        public void SharedPinsUseTheActiveProjectionAndIndependentPinsKeepTheirIdentity()
        {
            var work = new TabGroup { IsSaved = true, Name = "Work" };
            var personal = new TabGroup { IsSaved = true, Name = "Personal" };
            BrowserTab workProjection = AddPin(work, 101, TabPinScope.Profile);
            BrowserTab personalProjection = AddPin(personal, 101, TabPinScope.Profile);
            BrowserTab independentPin = AddPin(work, 102, TabPinScope.Workspace);

            BrowserTab?[] matches = TabSearchMatch.Find(new[] { work, personal }, personal, "example.test")
                .Select(match => match.Tab).ToArray();

            Assert.Collection(matches, tab => Assert.Same(personalProjection, tab),
                tab => Assert.Same(independentPin, tab));
            Assert.DoesNotContain(workProjection, matches);
        }

        private static BrowserTab AddPin(TabGroup group, long id, TabPinScope scope)
        {
            var tab = new BrowserTab { Id = id, Url = "https://example.test/inbox" };
            tab.SetPin(new PinnedPage(scope, tab.Url, tab.Title));
            group.Tabs.Add(tab);
            return tab;
        }
    }
}
