using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace Orion
{
    internal enum TabPinScope { Workspace, Profile }

    internal sealed record PinnedPage(TabPinScope Scope, string Url, string Title);

    /// <summary>
    /// Represents a browser tab with observable properties for data binding.
    /// </summary>
    public partial class BrowserTab : ObservableObject
    {
        /// <summary>
        /// Canonical URL of the start page (new-tab page). WebKit never navigates
        /// here — the frontend routes it to native content — so, like the titles
        /// below, its display metadata is defined here rather than coming from the
        /// engine.
        /// </summary>
        public const string StartPageUrl = "orion://newtab";

        /// <summary>
        /// Display titles for internal orion:// pages (WebKit never navigates there,
        /// so titles must be provided here rather than from a TitleChanged callback).
        /// </summary>
        private static readonly Dictionary<string, string> InternalPageTitles = new()
        {
            [StartPageUrl] = "Start Page",
            ["orion://settings"] = "Settings",
            ["orion://downloads"] = "Downloads",
        };

        /// <summary>
        /// Address-bar text for internal orion:// pages whose URL should not be shown
        /// verbatim. The start page shows an empty bar — revealing the "Search or
        /// enter address" placeholder, matching common browsers. URLs absent here
        /// show themselves.
        /// </summary>
        private static readonly Dictionary<string, string> InternalPageAddressBarText = new()
        {
            [StartPageUrl] = "",
        };

        public static bool IsStartPageUrl(string url) => url == StartPageUrl;

        /// <summary>
        /// True for URLs showing an empty tab (start page or blank tab) — content a
        /// navigation may replace rather than warranting a new tab.
        /// </summary>
        public static bool IsNewTabUrl(string url) => IsStartPageUrl(url) || url == "about:blank";

        /// <summary>
        /// The text shown in the address bar for a URL — an override from
        /// InternalPageAddressBarText if present, otherwise the URL itself.
        /// </summary>
        public static string AddressBarTextForUrl(string url) =>
            InternalPageAddressBarText.TryGetValue(url, out string? text) ? text : url;

        /// <summary>
        /// Database ID from the core module (TabId). 0 means not yet persisted.
        /// </summary>
        [ObservableProperty]
        public partial long Id { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayHeader))]
        public partial string Title { get; set; } = "Blank Tab";

        [ObservableProperty]
        public partial string Url { get; set; } = "about:blank";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayHeader))]
        [NotifyPropertyChangedFor(nameof(DisplayIsClosable))]
        [NotifyPropertyChangedFor(nameof(CloseButtonVisibility))]
        [NotifyPropertyChangedFor(nameof(TabMaxWidth))]
        public partial bool IsPinned { get; set; }

        internal PinnedPage? Pin { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VerticalTabVisibility))]
        internal partial bool IsInPinFolder { get; set; }

        public Visibility VerticalTabVisibility => IsInPinFolder ? Visibility.Collapsed : Visibility.Visible;

        internal void SetPin(PinnedPage? pin)
        {
            Pin = pin;
            IsPinned = pin != null;
        }

        partial void OnIsPinnedChanged(bool value)
        {
            if (value)
                Pin ??= new PinnedPage(TabPinScope.Workspace, Url, Title);
            else
                Pin = null;
        }

        public string DisplayHeader => IsPinned || !GlobalShowTitles ? "" : Title;

        public bool DisplayIsClosable => !IsPinned;

        public Visibility CloseButtonVisibility => IsPinned ? Visibility.Collapsed : Visibility.Visible;

        public double TabMaxWidth => IsPinned || !GlobalShowTitles ? 36 : 10000;

        [ObservableProperty]
        public partial long GroupId { get; set; }

        public bool IsInternalPage => Url.StartsWith("orion://");

        [ObservableProperty]
        public partial bool IsCrashed { get; set; }

        /// <summary>
        /// True while the engine reports the main frame loading: from the start
        /// of the provisional load until it finishes, fails, or the web process
        /// dies. Pushed by NativeWebView.IsLoadingChanged.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowsLoadingIndicator))]
        [NotifyPropertyChangedFor(nameof(FaviconIconSource))]
        [NotifyPropertyChangedFor(nameof(FaviconImageVisibility))]
        public partial bool IsLoading { get; set; }

        /// <summary>
        /// Whether the tab shows a loading spinner. It takes the favicon's slot,
        /// so it follows the favicon visibility setting.
        /// </summary>
        public bool ShowsLoadingIndicator => IsLoading && GlobalShowFavicons;

        /// <summary>Favicon image (or placeholder) visibility; hidden while the spinner shows.</summary>
        public Visibility FaviconImageVisibility => IsLoading ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// Latest captured page thumbnail as encoded JPEG bytes; null until
        /// the first capture. Updated when the tab stops being the active
        /// one (hidden views can't paint, so captures happen on switch-away).
        /// </summary>
        [ObservableProperty]
        public partial byte[]? ThumbnailJpeg { get; set; }

        /// <summary>
        /// ThumbnailJpeg decoded for binding (tab overview, hover preview).
        /// Kept in sync by OnThumbnailJpegChanged; null until first capture.
        /// </summary>
        [ObservableProperty]
        public partial ImageSource? ThumbnailImage { get; set; }

        partial void OnThumbnailJpegChanged(byte[]? value)
        {
            if (value == null)
            {
                ThumbnailImage = null;
                return;
            }
            _ = DecodeThumbnailAsync(value);
        }

        private async Task DecodeThumbnailAsync(byte[] jpeg)
        {
            try
            {
                // DecodePixelWidth caps decode cost/memory: CEF captures
                // arrive at native viewport width (up to 4K) while consumers
                // render tiles. The stream must stay alive until SetSourceAsync
                // completes — BitmapImage reads it during the await, so a
                // synchronous SetSource + dispose yields a blank image.
                var bitmap = new BitmapImage { DecodePixelWidth = 1024 };
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(jpeg);
                    _ = await writer.StoreAsync();
                    _ = writer.DetachStream();
                }
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                if (ReferenceEquals(ThumbnailJpeg, jpeg))
                    ThumbnailImage = bitmap;
            }
            catch (Exception ex)
            {
                LogError($"Thumbnail decode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True when the tab exists in the UI but has no live WebKit view.
        /// The tab retains its Title and Url but will reload on next activation.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FaviconIconSource))]
        public partial ImageSource? FaviconImage { get; set; }

        public static bool GlobalShowFavicons { get; set; } = true;
        public static bool GlobalShowTitles { get; set; } = true;

        public IconSource? FaviconIconSource =>
            GlobalShowFavicons && !IsLoading && FaviconImage is ImageSource src ? new ImageIconSource { ImageSource = src } : null;

        public Visibility FaviconVisibility => GlobalShowFavicons ? Visibility.Visible : Visibility.Collapsed;

        public Visibility TitleVisibility => GlobalShowTitles ? Visibility.Visible : Visibility.Collapsed;

        public void NotifyFaviconVisibilityChanged()
        {
            OnPropertyChanged(nameof(FaviconIconSource));
            OnPropertyChanged(nameof(FaviconVisibility));
            OnPropertyChanged(nameof(ShowsLoadingIndicator));
        }

        public void NotifyTitleVisibilityChanged()
        {
            OnPropertyChanged(nameof(DisplayHeader));
            OnPropertyChanged(nameof(TitleVisibility));
            OnPropertyChanged(nameof(TabMaxWidth));
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TitleOpacity))]
        public partial bool IsHibernated { get; set; }

        /// <summary>
        /// Opacity for the tab title. Dimmed when hibernated so users can
        /// see at a glance which tabs are active vs unloaded.
        /// </summary>
        public double TitleOpacity => IsHibernated ? 0.5 : 1.0;

        partial void OnUrlChanged(string value)
        {
            if (InternalPageTitles.TryGetValue(value, out string? title))
                Title = title;
        }

        // Collection of child items. Used for nested nodes in the TreeView.
        public ObservableCollection<BrowserTab> Children { get; set; } = [];

        public static async Task<ImageSource?> CreateFaviconImageAsync(byte[] data, string? url = null)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(data);
                    _ = await writer.StoreAsync();
                    _ = writer.DetachStream();
                }
                stream.Seek(0);

                if (url != null && url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var svg = new SvgImageSource();
                    _ = await svg.SetSourceAsync(stream);
                    return svg;
                }

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
