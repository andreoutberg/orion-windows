using Microsoft.UI.Xaml;
using System.Collections.Generic;
using Xunit;

namespace Orion
{
    public class BrowserTabTests
    {
        [Fact]
        public void DefaultTitle_IsBlankTab()
        {
            var tab = new BrowserTab();
            Assert.Equal("Blank Tab", tab.Title);
        }

        [Fact]
        public void DefaultUrl_IsAboutBlank()
        {
            var tab = new BrowserTab();
            Assert.Equal("about:blank", tab.Url);
        }

        [Fact]
        public void IsInternalPage_FalseForAboutBlank()
        {
            var tab = new BrowserTab { Url = "about:blank" };
            Assert.False(tab.IsInternalPage);
        }

        [Fact]
        public void IsInternalPage_TrueForOrionScheme()
        {
            var tab = new BrowserTab { Url = "orion://settings" };
            Assert.True(tab.IsInternalPage);
        }

        [Theory]
        [InlineData("orion://settings", "Settings")]
        [InlineData("orion://downloads", "Downloads")]
        public void SettingInternalUrl_UpdatesTitle(string url, string expectedTitle)
        {
            var tab = new BrowserTab { Url = url };
            Assert.Equal(expectedTitle, tab.Title);
        }

        [Fact]
        public void SettingExternalUrl_DoesNotChangeTitle()
        {
            var tab = new BrowserTab { Title = "My Tab", Url = "https://example.com" };
            Assert.Equal("My Tab", tab.Title);
        }

        [Fact]
        public void StartPageMarkFollowsTheCurrentUrl()
        {
            var tab = new BrowserTab();
            var changed = new List<string>();
            tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

            tab.Url = BrowserTab.StartPageUrl;
            Assert.Equal(Visibility.Visible, tab.StartPageIconVisibility);
            Assert.Equal(Visibility.Collapsed, tab.SiteIconVisibility);
            Assert.Contains(nameof(BrowserTab.StartPageIconVisibility), changed);
            Assert.Contains(nameof(BrowserTab.SiteIconVisibility), changed);

            tab.Url = "https://example.com/";
            Assert.Equal(Visibility.Collapsed, tab.StartPageIconVisibility);
            Assert.Equal(Visibility.Visible, tab.SiteIconVisibility);
        }

        [Theory]
        [InlineData("orion://newtab", true)]
        [InlineData("about:blank", true)]
        [InlineData("https://example.com", false)]
        [InlineData("orion://settings", false)]
        public void IsNewTabUrl_MatchesEmptyTabPages(string url, bool expected)
        {
            Assert.Equal(expected, BrowserTab.IsNewTabUrl(url));
        }

        [Fact]
        public void SetIsHibernated_FiresPropertyChanged()
        {
            var tab = new BrowserTab();
            var raised = new List<string>();
            tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            tab.IsHibernated = true;

            Assert.Contains(nameof(BrowserTab.IsHibernated), raised);
        }

        [Fact]
        public void IsCrashed_DefaultIsFalse()
        {
            var tab = new BrowserTab();
            Assert.False(tab.IsCrashed);
        }

        [Fact]
        public void SetIsCrashed_FiresPropertyChanged()
        {
            var tab = new BrowserTab();
            var raised = new List<string>();
            tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            tab.IsCrashed = true;

            Assert.Contains(nameof(BrowserTab.IsCrashed), raised);
        }

        [Fact]
        public void IsCrashed_PreservesUrlAndTitle()
        {
            var tab = new BrowserTab
            {
                Title = "Example",
                Url = "https://example.com",
                IsCrashed = true
            };

            Assert.Equal("Example", tab.Title);
            Assert.Equal("https://example.com", tab.Url);
        }

        [Fact]
        public void IsCrashed_CanBeCleared()
        {
            var tab = new BrowserTab
            {
                IsCrashed = true
            };

            tab.IsCrashed = false;

            Assert.False(tab.IsCrashed);
        }

        [Fact]
        public void IsLoading_DefaultIsFalse()
        {
            var tab = new BrowserTab();

            Assert.False(tab.IsLoading);
            Assert.False(tab.ShowsLoadingIndicator);
            Assert.Equal(Visibility.Visible, tab.FaviconImageVisibility);
        }

        [Fact]
        public void SetIsLoading_NotifiesFaviconSlotProperties()
        {
            var tab = new BrowserTab();
            var raised = new List<string>();
            tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            tab.IsLoading = true;

            Assert.Contains(nameof(BrowserTab.IsLoading), raised);
            Assert.Contains(nameof(BrowserTab.ShowsLoadingIndicator), raised);
            Assert.Contains(nameof(BrowserTab.FaviconIconSource), raised);
            Assert.Contains(nameof(BrowserTab.FaviconImageVisibility), raised);
        }

        [Fact]
        public void IsLoading_SwapsFaviconForSpinner()
        {
            var tab = new BrowserTab { IsLoading = true };

            Assert.True(tab.ShowsLoadingIndicator);
            Assert.Equal(Visibility.Collapsed, tab.FaviconImageVisibility);
            Assert.Null(tab.FaviconIconSource);

            tab.IsLoading = false;

            Assert.False(tab.ShowsLoadingIndicator);
            Assert.Equal(Visibility.Visible, tab.FaviconImageVisibility);
        }
    }
}
