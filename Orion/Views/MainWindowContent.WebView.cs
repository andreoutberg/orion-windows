using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.System;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        private const int MaxFaviconBytes = 512 * 1024;
        private static readonly byte[] s_pdfMagic = [(byte)'%', (byte)'P', (byte)'D', (byte)'F'];

        private static readonly HttpClient s_faviconHttpClient = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // settings::privacy::disable_history, cached because the consumer runs
        // inside an engine callback (TitleChanged), where GetSetting must not
        // be called — it can pump the run loop, which is only allowed from
        // top-level entry points.
        private bool _historyDisabled;
        private OrionBackend.SettingChangedCallback? _disableHistoryObserver;
        private long _disableHistoryObserverId;

        private void InitializeHistoryRecording()
        {
            _historyDisabled = OrionBackend.GetBoolSetting(ViewModel.ActiveProfileId,
                "settings::privacy::disable_history", "settings::privacy", "disable_history", false);
            _disableHistoryObserver = (profileId, _, newValue) =>
            {
                if (profileId == ViewModel.ActiveProfileId)
                    _historyDisabled = newValue == "1";
            };
            _disableHistoryObserverId = OrionBackend.AddSettingObserver(
                "settings::privacy::disable_history", _disableHistoryObserver);
        }

        private void UnregisterHistoryRecording()
        {
            OrionBackend.RemoveSettingObserver(_disableHistoryObserverId);
            _disableHistoryObserverId = 0;
            _disableHistoryObserver = null;
        }

        private void OnWebViewHostLoaded(object sender, RoutedEventArgs e)
        {
            void UpdateCrashOverlaySize()
            {
                CrashOverlay.Width = WebViewHost.ActualWidth;
                CrashOverlay.Height = WebViewHost.ActualHeight;
            }
            WebViewHost.SizeChanged += (_, _) => UpdateCrashOverlaySize();
            CrashBannerPopup.Opened += (_, _) => UpdateCrashOverlaySize();

            ViewModel.WebViewFactory = CreateNativeWebView;

            // Handle initial active tab (PropertyChanged didn't fire during ViewModel constructor)
            BrowserTab? activeTab = ViewModel.ActiveTab;
            if (activeTab != null)
                OnActiveTabChanged(activeTab);
        }

        /// <summary>
        /// Factory implementation for creating NativeWebView instances.
        /// Wires native events to update the tab's observable properties.
        /// Set as ViewModel.WebViewFactory once the host panel is ready.
        /// </summary>
        private void WireWebViewEvents(NativeWebView webView, BrowserTab tab)
        {
            webView.TitleChanged += title =>
            {
                if (tab.IsCrashed) return;
                tab.Title = title;
                if (!string.IsNullOrEmpty(tab.Url) && !string.IsNullOrEmpty(title))
                {
                    if (!_historyDisabled)
                        OrionBackend.AddHistoryEntry(ViewModel.ActiveProfileId, tab.Url, title);
                    if (tab.Id != 0 && !tab.IsPinned)
                        OrionBackend.UpdateTabName(tab.Id, title);
                }
            };
            webView.ActiveURLChanged += url =>
            {
                if (tab.IsCrashed) return;
                tab.Url = url;
                AutofillPicker.Close();
                CloseWebContextMenu();
                if (tab == ViewModel.ActiveTab)
                {
                    AddressBarTextBox.Text = BrowserTab.AddressBarTextForUrl(url);
                    // Only the active tab can own an open prompt.
                    PermissionPromptControl.DismissForNavigation(url);
                }
                if (tab.Id != 0 && !tab.IsPinned)
                    OrionBackend.UpdateTabUri(tab.Id, url);
            };
            webView.UnhandledKeyEvent += (vkCode, modifiers) =>
                OnEngineUnhandledKey((VirtualKey)vkCode, (VirtualKeyModifiers)modifiers);
            webView.ReservedKeyEvent += (vkCode, modifiers) =>
                OnEngineReservedKey((VirtualKey)vkCode, (VirtualKeyModifiers)modifiers);
            webView.ActiveURLChanged += url => _ = LoadCachedFaviconAsync(tab);
            webView.FaviconUrlChanged += (pageUrl, iconUrls) =>
                _ = DownloadAndStoreFaviconAsync(tab, pageUrl, iconUrls.Split('\n'));
            webView.NavigationError += (category, rawCode, failingUrl, description) =>
            {
                ErrorPageType type = Enum.IsDefined(typeof(ErrorPageType), category)
                    ? (ErrorPageType)category : ErrorPageType.Unknown;
                return ErrorPageProvider.GetErrorPage(type, failingUrl, description);
            };
            webView.WebProcessTerminated += (processName, reason, reasonStr) =>
            {
                LogError($"{processName} terminated: {reasonStr} ({reason})");
                tab.IsCrashed = true;
                CloseWebContextMenu();
            };
            webView.NavigationStateChanged += (canGoBack, canGoForward) =>
            {
                if (ViewModel.FindTabViewModel(tab) is { } vm)
                {
                    vm.CanGoBack = canGoBack;
                    vm.CanGoForward = canGoForward;
                }
            };
            webView.IsLoadingChanged += isLoading => tab.IsLoading = isLoading;
            tab.IsLoading = webView.IsLoading;
            webView.DevToolsViewChanged += (previous, current) => OnDevToolsViewChanged(tab, previous, current);
            // window.close() arrives from inside an engine callback and
            // closing the tab destroys the web view, so defer the teardown
            // to the next dispatcher tick.
            webView.CloseRequested += () =>
                _ = DispatcherQueue.TryEnqueue(() => CloseTab(tab));
            webView.AuthCredentialsRequested += (host, port, isProxy, realm, requestId) =>
                _ = DispatcherQueue.TryEnqueue(
                    () => ShowHttpAuthDialog(host, port, isProxy, realm, requestId));
            webView.InputFocus += json => OnAutofillInputFocus(webView, tab, json);
            webView.FormSubmit += json => OnAutofillFormSubmit(webView, tab, json);
            webView.ElementFullscreenChanged += isFullscreen =>
                OnElementFullscreenChanged(webView, isFullscreen);
            webView.ContextMenuRequested += context =>
                !tab.IsCrashed && EnsureWebContextMenu().HandleRequest(webView, context);
            webView.FindResultChanged += (matchCount, activeMatchOrdinal) =>
            {
                if (tab == ViewModel.ActiveTab)
                    FindBar.SetResult(matchCount, activeMatchOrdinal);
            };
            webView.SnapshotTaken += jpeg =>
            {
                // null = capture failed (e.g. the view was already hidden);
                // keep the previous thumbnail rather than clearing it.
                if (jpeg != null)
                    tab.ThumbnailJpeg = jpeg;
            };
            webView.MediaAccessPermissionRequested += (pageUrl, types, requestId) =>
            {
                // Prompts only for the active tab; an inactive tab's request
                // is denied once and the site re-asks on user interaction.
                if (tab != ViewModel.ActiveTab)
                {
                    OrionBackend.RespondToMediaAccessPermissionRequest(requestId, false);
                }
                else
                {
                    PermissionPromptControl.HandleRequest(WebsiteSettingsButton, webView,
                        ViewModel.ActiveProfileId, pageUrl, types, requestId);
                }
            };


            string? ua = MainWindowViewModel.GetUserAgentString(ViewModel.ActiveProfileId);
            if (ua != null)
                webView.SetUserAgent(ua);
        }

        private NativeWebView CreateNativeWebView(BrowserTab tab)
        {
            Win32.HWND parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(ParentWindow);
            var webView = new NativeWebView(parentHwnd, ViewModel.ActiveProfileId);
            WireWebViewEvents(webView, tab);
            return webView;
        }

        /// <summary>
        /// Creates a new tab for a window.open that should open as a tab (not a popup).
        /// Returns the parent HWND + callbacks for native to create the WKView under.
        /// The tab's NativeWebView is detached — call AttachNewTabWebView after native
        /// creates the child HWND to finish wiring it up.
        /// </summary>
        internal (Win32.HWND parentHwnd, OrionBackend.BrowserCallbacks callbacks, BrowserTab tab)?
            CreateNewTabForWindowOpen()
        {
            if (ParentWindow == null || ViewModel.WebViewFactory == null)
                return null;

            BrowserTab? tab = ViewModel.CreateNewTab("about:blank", insertAfterActive: true);
            if (tab == null)
                return null;

            var webView = NativeWebView.CreateDetached();
            WireWebViewEvents(webView, tab);

            BrowserTabViewModel tabVm = ViewModel.GetOrCreateTabViewModel(tab);
            tabVm.NativeWebView = webView;

            Win32.HWND parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(ParentWindow);
            return (parentHwnd, webView.Callbacks, tab);
        }

        /// <summary>
        /// Opens a new unfocused tab and eagerly loads the URL in a hidden web
        /// view, so title and favicon populate while the current tab stays
        /// active. Falls back to lazy loading (first activation navigates via
        /// OnActiveTabChanged) when the host panel isn't ready.
        /// </summary>
        internal void OpenNewBackgroundTab(string url, bool insertAfterActive, string? title = null)
        {
            BrowserTab? tab = ViewModel.CreateNewTab(url, title, insertAfterActive: insertAfterActive, activate: false);
            if (tab == null || ParentWindow == null || ViewModel.WebViewFactory == null)
                return;

            try
            {
                NativeWebView webView = ViewModel.WebViewFactory(tab);
                ViewModel.GetOrCreateTabViewModel(tab).NativeWebView = webView;
                _ = WebViewHost.Add(webView.Hwnd);

                // Size the hidden view to the panel so the page lays out at
                // the real viewport; Show() repositions again on activation.
                double scale = WebViewHost.XamlRoot?.RasterizationScale ?? 1.0;
                int width = (int)(WebViewHost.ActualWidth * scale);
                int height = (int)(WebViewHost.ActualHeight * scale);
                if (width > 0 && height > 0)
                    webView.Resize(width, height);

                webView.LoadUrl(url);
            }
            catch (Exception ex)
            {
                LogError($"OpenNewBackgroundTab: failed to create web view: {ex.Message}");
            }
        }

        /// <summary>
        /// Called after native creates the WKView HWND for a window.open new-tab.
        /// Attaches the HWND to the tab's NativeWebView and registers it in the host panel.
        /// </summary>
        internal void AttachNewTabWebView(BrowserTab tab, Win32.HWND webViewHwnd)
        {
            BrowserTabViewModel tabVm = ViewModel.GetOrCreateTabViewModel(tab);
            NativeWebView? webView = tabVm.NativeWebView;
            if (webView == null)
            {
                LogError("AttachNewTabWebView: no NativeWebView on tab");
                return;
            }

            webView.AttachHwnd(webViewHwnd);
            WebViewHost.Add(webViewHwnd);
            HeroImage.Visibility = Visibility.Collapsed;

            // If this is the active tab, show its HWND and resize
            if (tab == ViewModel.ActiveTab)
            {
                WebViewHost.Show(webViewHwnd);
                double scale = WebViewHost.XamlRoot?.RasterizationScale ?? 1.0;
                int width = (int)(WebViewHost.ActualWidth * scale);
                int height = (int)(WebViewHost.ActualHeight * scale);
                if (width > 0 && height > 0)
                    webView.Resize(width, height);
            }
        }

        /// <summary>
        /// Ensures the tab has a per-window NativeWebView and its HWND is registered
        /// in the host panel. No-op for internal pages or if the factory isn't ready yet.
        /// </summary>
        private void EnsureWebView(BrowserTab tab)
        {
            if (tab.IsInternalPage || ViewModel.WebViewFactory == null)
                return;

            BrowserTabViewModel tabVm = ViewModel.GetOrCreateTabViewModel(tab);
            if (tabVm.NativeWebView != null)
                return;

            try
            {
                tabVm.NativeWebView = ViewModel.WebViewFactory(tab);
                bool added = WebViewHost.Add(tabVm.NativeWebView.Hwnd);
                LogDebug(
                    $"EnsureWebView: HWND={tabVm.NativeWebView.Hwnd}, Added={added}, " +
                    $"IsAttached={WebViewHost.IsAttached}, VisibleHwnd={WebViewHost.VisibleHwnd}");
                HeroImage.Visibility = Visibility.Collapsed;

                // Initial resize to match container
                double scale = WebViewHost.XamlRoot?.RasterizationScale ?? 1.0;
                int width = (int)(WebViewHost.ActualWidth * scale);
                int height = (int)(WebViewHost.ActualHeight * scale);
                LogDebug(
                    $"EnsureWebView: Resize {width}x{height} (scale={scale}, panel={WebViewHost.ActualWidth}x{WebViewHost.ActualHeight})");
                if (width > 0 && height > 0)
                    tabVm.NativeWebView.Resize(width, height);
            }
            catch (Exception ex)
            {
                LogError($"Failed to create WebKit view: {ex.Message}");
            }
        }

        private static readonly Regex s_sizeRegex = new(@"(\d+)x\1", RegexOptions.Compiled);

        private static int ScoreFaviconUrl(string url)
        {
            int score = 0;

            string path = url;
            try { path = new Uri(url).AbsolutePath; } catch { }
            string ext = Path.GetExtension(path).ToLowerInvariant();

            // Format preference: PNG and ICO render reliably at small sizes.
            // SVG can fail on complex content in WinUI's SvgImageSource.
            score += ext switch
            {
                ".png" => 30,
                ".ico" => 20,
                ".webp" => 10,
                ".gif" => 5,
                ".jpg" or ".jpeg" => 5,
                ".svg" => 0,
                _ => 10, // unknown extension (e.g. dynamic URL) — neutral
            };

            // Size preference: closest to 32px is ideal for 14-16px tab display.
            Match m = s_sizeRegex.Match(path);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int size))
            {
                score += size switch
                {
                    32 => 20,
                    16 => 15,
                    48 => 10,
                    64 => 5,
                    _ when size >= 128 => -10, // oversized
                    _ => 0,
                };
            }

            return score;
        }

        private async Task<byte[]?> FetchFaviconBytesAsync(string iconUrl)
        {
            if (iconUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int commaIndex = iconUrl.IndexOf(',');
                if (commaIndex < 0)
                    return null;
                string meta = iconUrl[5..commaIndex];
                string encoded = iconUrl[(commaIndex + 1)..];
                if (!meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return null;
                byte[] data = Convert.FromBase64String(encoded);
                if (data.Length > MaxFaviconBytes)
                {
                    LogDebug($"Favicon too large (>{MaxFaviconBytes} bytes): data: URI");
                    return null;
                }
                return data.Length > 0 ? data : null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, iconUrl);
            string? ua = MainWindowViewModel.GetUserAgentString(ViewModel.ActiveProfileId);
            if (ua != null)
                _ = request.Headers.TryAddWithoutValidation("User-Agent", ua);
            using HttpResponseMessage response = await s_faviconHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            using Stream stream = await response.Content.ReadAsStreamAsync();
            byte[] buf = new byte[MaxFaviconBytes + 1];
            int total = 0, read;
            while (total < buf.Length && (read = await stream.ReadAsync(buf, total, buf.Length - total)) > 0)
                total += read;
            if (total > MaxFaviconBytes)
            {
                LogDebug($"Favicon too large (>{MaxFaviconBytes} bytes)");
                return null;
            }
            if (total == 0)
                return null;
            byte[] result = new byte[total];
            Array.Copy(buf, result, total);
            return result;
        }

        private async Task DownloadAndStoreFaviconAsync(BrowserTab tab, string pageUrl, string[] iconUrls)
        {
            if (PrivateBrowsing.IsActive)
                return;

            // Favicons are downloaded over HTTP(S) only.
            iconUrls = Array.FindAll(iconUrls, u =>
                Uri.TryCreate(u, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
            if (iconUrls.Length == 0)
                return;

            Array.Sort(iconUrls, (a, b) => ScoreFaviconUrl(b).CompareTo(ScoreFaviconUrl(a)));


            foreach (string iconUrl in iconUrls)
            {
                try
                {
                    byte[]? data = await FetchFaviconBytesAsync(iconUrl);
                    if (data == null)
                        continue;
                    if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(s_pdfMagic))
                    {
                        LogDebug("Favicon rejected (PDF)");
                        continue;
                    }
                    Microsoft.UI.Xaml.Media.ImageSource? image =
                        await BrowserTab.CreateFaviconImageAsync(data, iconUrl);
                    if (image == null)
                    {
                        LogDebug("Favicon rejected (failed to decode)");
                        continue;
                    }
                    OrionBackend.SetFavicon(pageUrl, iconUrl, data);
                    tab.FaviconImage = image;
                    return;
                }
                catch (Exception ex)
                {
                    LogDebug($"Favicon download failed ({ex.GetType().Name})");
                }
            }
        }

        private static async Task LoadCachedFaviconAsync(BrowserTab tab)
        {
            if (string.IsNullOrEmpty(tab.Url) || tab.Url == "about:blank")
                return;
            OrionBackend.FaviconResult? result = OrionBackend.GetFavicon(tab.Url);
            if (result != null)
                tab.FaviconImage = await BrowserTab.CreateFaviconImageAsync(result.Data, result.IconUrl);
        }

    }
}
