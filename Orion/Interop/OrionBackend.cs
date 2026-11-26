using DirectShow;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Orion
{
    //[StructLayout(LayoutKind.Sequential)]
    //public struct RECT
    //{
    //    public int Left;
    //    public int Top;
    //    public int Right;
    //    public int Bottom;

    //    public RECT(int left, int top, int right, int bottom)
    //    {
    //        Left = left;
    //        Top = top;
    //        Right = right;
    //        Bottom = bottom;
    //    }
    //}

    [StructLayout(LayoutKind.Sequential)]
    internal struct InstalledExtensionNative
    {
        public IntPtr id;       // char* UTF-8
        public IntPtr name;     // char* UTF-8
        public IntPtr version;  // char* UTF-8
        public int type;        // ExtensionType: 1=firefox, 2=chrome
        public sbyte is_enabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtensionToolbarItemNative
    {
        public IntPtr id;            // char* UTF-8
        public IntPtr title;         // char* UTF-8
        public IntPtr popup_uri;     // char* UTF-8
        public IntPtr icon_data;     // uint8_t*
        public int icon_data_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProfileNative
    {
        public long id;
        public IntPtr name;
        public IntPtr icon_name;
        public sbyte show_in_address_bar;
        public sbyte show_badge_in_app_icon;
        public IntPtr keyboard_shortcut;
        public sbyte is_internal;
        public sbyte is_default;
        public IntPtr engine;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowNative
    {
        public long id;
        public long profile_id;
        public IntPtr name;
        public sbyte is_tab_group;
        public long selected_tab_id;
        public sbyte has_ui_window;
        public sbyte is_private;
        public IntPtr keyboard_shortcut;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TabNative
    {
        public long id;
        public long window_id;
        public IntPtr name;
        public IntPtr uri;
        public sbyte is_pinned;
        public long parent_tab_id;
        public sbyte is_collapsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecentlyClosedTabNative
    {
        public long id;
        public long profile_id;
        public IntPtr name;
        public IntPtr uri;
        public long closed_at;
        public long window_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecentlyClosedWindowNative
    {
        public long id;
        public long profile_id;
        public IntPtr name;
        public long closed_at;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DownloadEntryNative
    {
        public long id;
        public long profile_id;
        public IntPtr name;
        public IntPtr path;
        public int operation_state;
        public long timestamp;
        public long total_size;
        public long current_size;
        public IntPtr origin_uri;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PasswordItemNative
    {
        public long Id;
        public long ProfileId;
        public IntPtr Url;
        public IntPtr Username;
        public IntPtr Password;
        public IntPtr VerificationKey;
        public IntPtr VerificationCode;
    }

    public static class OrionBackend
    {
        // Delegate definitions
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProgressChangedCallback(double progress);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LoadingStateChangedCallback(int isLoading);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ActiveURLChangedCallback([MarshalAs(UnmanagedType.LPWStr)] string url);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TitleChangedCallback([MarshalAs(UnmanagedType.LPWStr)] string title);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int UnhandledKeyEventCallback(uint vkCode, uint modifiers);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ReservedKeyEventCallback(uint vkCode, uint modifiers);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FaviconUrlChangedCallback(
            [MarshalAs(UnmanagedType.LPWStr)] string pageUrl,
            [MarshalAs(UnmanagedType.LPWStr)] string iconUrls);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPWStr)]
        public delegate string? NavigationErrorCallback(
            int errorCategory, int rawErrorCode,
            [MarshalAs(UnmanagedType.LPWStr)] string failingUrl,
            [MarshalAs(UnmanagedType.LPWStr)] string description);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void WebProcessTerminatedCallback(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string processName,
            uint reason,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string reasonString);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InputFocusCallback(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string jsonPayload);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FormSubmitCallback(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string jsonPayload);

        /// <summary>
        /// Fired when the page enters or leaves HTML5 element fullscreen
        /// (e.g. a fullscreen video). Nonzero = entered.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ElementFullscreenChangedCallback(int isFullscreen);

        /// <summary>
        /// Fired when the engine wants to show a context menu. The pointer is
        /// a ContextMenuRequestNative valid only during the call — copy
        /// immediately. Return 1 to suppress the engine's native menu (the
        /// app shows its own), 0 to let the engine show its default menu.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ContextMenuRequestedCallback(IntPtr request);

        /// <summary>
        /// Fired with the result of a find-in-page operation.
        /// matchCount: 0 = no matches; negative = more than |matchCount|
        /// matches (the engine's counting cap; the exact count is unknown).
        /// activeMatchOrdinal: 1-based index of the current match, 0 = unknown.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FindResultCallback(int matchCount, int activeMatchOrdinal);

        /// <summary>
        /// Fired with the result of a RequestTabSnapshot call: the view's
        /// visible viewport as encoded JPEG bytes, or IntPtr.Zero/0 when the
        /// capture failed (e.g. the view is hidden). The buffer is only
        /// valid during the call — copy immediately.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SnapshotTakenCallback(IntPtr data, int size);

        /// <summary>
        /// Fired when the page requests media capture access (getUserMedia /
        /// getDisplayMedia). pageUrl is the view's main-frame URL,
        /// mediaPermissions a WebMediaPermission bitmask. Every request must
        /// be resolved exactly once via RespondToMediaAccessPermissionRequest;
        /// the engine denies requests whose view goes away unanswered.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MediaAccessPermissionRequestedCallback(
            [MarshalAs(UnmanagedType.LPWStr)] string pageUrl,
            int mediaPermissions, long requestId);

        /// <summary>
        /// Fired whenever the view's back/forward availability changes
        /// (navigation commits, back-forward list mutations).
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NavigationStateChangedCallback(int canGoBack, int canGoForward);

        /// <summary>
        /// Fired when the view's DevTools frontend view appears or goes away.
        /// A non-zero handle is a hidden child of the view's top-level window
        /// that the app hosts and positions from then on (repeated when the
        /// frontend asks to be brought to the front); zero means it closed.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DevToolsViewChangedCallback(Win32.HWND devToolsHwnd);

        /// <summary>
        /// The page requested to close itself (window.close()) and the engine
        /// allowed it; close the hosting tab or popup window.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CloseRequestedCallback();

        /// <summary>
        /// A request hit an HTTP auth challenge (401 from the server, 407
        /// from a proxy when isProxy is nonzero; realm may be empty). Show a
        /// credentials dialog and resolve exactly once via
        /// RespondToAuthCredentials; the engine cancels challenges whose view
        /// goes away unanswered.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void AuthCredentialsRequestedCallback(
            [MarshalAs(UnmanagedType.LPWStr)] string host,
            int port, int isProxy,
            [MarshalAs(UnmanagedType.LPWStr)] string realm,
            long requestId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DownloadChangedCallback(IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DownloadDeletedCallback(long downloadId);

        /// <summary>
        /// Fired on the UI thread after an observed setting's new value is committed.
        /// newValue uses the raw stored representation (booleans are "1"/"0").
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SettingChangedCallback(
            long profileId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newValue);

        /// <summary>
        /// Core asks the host to display a notification; requestJson is the
        /// UTF-8 JSON of orion::NotificationRequest (snake_case fields).
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ShowNotificationCallback(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestJson);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ClearNotificationCallback(
            int source,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string notificationId);

        // Matching struct. Field order mirrors BrowserCallbacks in
        // OrionBackend/browser_engine_types.h by hand — keep both sides in
        // sync, appending new fields at the end only.
        [StructLayout(LayoutKind.Sequential)]
        public struct BrowserCallbacks
        {
            public ProgressChangedCallback? ProgressChanged;
            public LoadingStateChangedCallback? LoadingStateChanged;
            public ActiveURLChangedCallback? ActiveURLChanged;
            public TitleChangedCallback? TitleChanged;
            public UnhandledKeyEventCallback? UnhandledKeyEvent;
            public ReservedKeyEventCallback? ReservedKeyEvent;
            public FaviconUrlChangedCallback? FaviconUrlChanged;
            public NavigationErrorCallback? NavigationError;
            public WebProcessTerminatedCallback? WebProcessTerminated;
            public InputFocusCallback? InputFocus;
            public FormSubmitCallback? FormSubmit;
            public ElementFullscreenChangedCallback? ElementFullscreenChanged;
            public ContextMenuRequestedCallback? ContextMenuRequested;
            public FindResultCallback? FindResult;
            public SnapshotTakenCallback? SnapshotTaken;
            public MediaAccessPermissionRequestedCallback? MediaAccessPermissionRequested;
            public NavigationStateChangedCallback? NavigationStateChanged;
            public DevToolsViewChangedCallback? DevToolsViewChanged;
            public CloseRequestedCallback? CloseRequested;
            public AuthCredentialsRequestedCallback? AuthCredentialsRequested;
        }

        // ───── Popup (window.open) support ────────────────────────────────────
        // Native calls CreatePopupCallback synchronously from the WebKit UI
        // client.  C# creates a PopupWindow and returns the HwndHostPanel's
        // parent HWND plus a fresh BrowserCallbacks struct (kept alive by the
        // PopupWindow itself so the delegates survive while native holds them).
        // Returning parentHwnd=0 means "deny this popup".
        [StructLayout(LayoutKind.Sequential)]
        public struct PopupCreateResult
        {
            public Win32.HWND ParentHwnd;
            public BrowserCallbacks Callbacks;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate PopupCreateResult CreatePopupCallback(
            int requestedWidth, int requestedHeight,
            int requestedX, int requestedY,
            sbyte wantLocationBar, sbyte wantResizable,
            sbyte wantsPopup, sbyte userGesture,
            Win32.HWND originatingHwnd,
            [MarshalAs(UnmanagedType.LPWStr)] string targetUrl);

        // Called after native creates the popup's WKView HWND so that C# can
        // register it inside the PopupWindow's HwndHostPanel and make it
        // visible.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PopupReadyCallback(Win32.HWND parentHwnd, Win32.HWND webViewHwnd);

        // Where a navigation diverted out of the current view should open.
        // Values must stay in sync with OpenDisposition in browser_engine_types.h.
        public enum OpenDisposition
        {
            NewForegroundTab = 0,
            NewBackgroundTab = 1,
            NewWindow = 2,
        }

        // Called when a navigation should open outside the current view: a
        // modifier-clicked link (Ctrl/Shift/middle) or a featureless
        // window.open / target=_blank that CEF routes to a tab. The
        // resulting view is standalone (no opener link).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OpenURLCallback(Win32.HWND originatingHwnd,
            [MarshalAs(UnmanagedType.LPWStr)] string targetUrl, int disposition);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static bool _initialized = false;

        /// <summary>
        /// Call this before any other OrionBackend methods to set up DLL search paths.
        /// </summary>
        public static void Initialize(string? dllDirectory = null)
        {
            if (_initialized)
                return;

            _initialized = true;

            // If no directory specified, use the app's base directory
            if (string.IsNullOrEmpty(dllDirectory))
                dllDirectory = AppContext.BaseDirectory;

            // Insert the app directory into the DLL search path (between the app dir and System32).
            // Do NOT call SetDefaultDllDirectories — that removes %PATH% from the search, which
            // breaks NVIDIA/AMD GPU driver DLL loading from DriverStore paths (e.g. nvgpucomp64.dll),
            // causing D3D/DComp device creation to fail with E_INVALIDARG in WinUI 3.
            _ = SetDllDirectory(dllDirectory);

#pragma warning disable RS0030 // Pre-logging bootstrap message
            System.Diagnostics.Debug.WriteLine($"OrionBackend: DLL directory set to {dllDirectory}");
#pragma warning restore RS0030
        }

        private const string OrionDll = "OrionBackend.dll";

        private static class Dll
        {
            [DllImport(OrionDll)]
            public static extern int CheckBrowserSubprocess();

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string LoadBrowserEngine([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport(OrionDll)]
            public static extern int GetBrowserEngine();

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string GetOrionVersion();

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string InitOrionCore(long profileId);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.I1)]
            public static extern bool ConfigurePrivateSession();

            [DllImport(OrionDll)]
            public static extern void ShutdownOrionCore();

            [DllImport(OrionDll)]
            public static extern int TickOrionCore();

            [DllImport(OrionDll)]
            public static extern Win32.HANDLE GetWakeupEventHandle();

            [DllImport(OrionDll)]
            public static extern Win32.HANDLE GetCefWakeupEventHandle();

            [DllImport(OrionDll)]
            public static extern void PumpCefMessageLoop();

            [DllImport(OrionDll)]
            public static extern Win32.HWND CreateOrionWindow(Win32.HWND parentWindow, BrowserCallbacks browserCallbacks, long profileId);

            [DllImport(OrionDll)]
            public static extern Win32.HWND CreateOrionWindowWithUrl(Win32.HWND parentWindow, BrowserCallbacks browserCallbacks, long profileId, [MarshalAs(UnmanagedType.LPUTF8Str)] string url);

            [DllImport(OrionDll)]
            public static extern void SetCreatePopupCallback(CreatePopupCallback cb);

            [DllImport(OrionDll)]
            public static extern void SetPopupReadyCallback(PopupReadyCallback cb);

            [DllImport(OrionDll)]
            public static extern void SetOpenURLCallback(OpenURLCallback cb);

            [DllImport(OrionDll)]
            public static extern void DestroyOrionWindow(Win32.HWND hwnd);

            [DllImport(OrionDll)]
            public static extern void ResizeOrionWindow(Win32.HWND hwnd, int width, int height);

            [DllImport(OrionDll)]
            public static extern void RequestExitElementFullscreen(Win32.HWND hwnd);

            [DllImport(OrionDll)]
            public static extern void AddHistoryEntry(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

            [DllImport(OrionDll)]
            public static extern IntPtr GetRecentHistory(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeHistoryEntries(IntPtr entries);

            [DllImport(OrionDll)]
            public static extern IntPtr GetHistoryForMenu(long profileId, int days, out int outCount);

            [DllImport(OrionDll)]
            public static extern void ClearHistory(long profileId);

            [DllImport(OrionDll)]
            public static extern void DeleteHistoryEntries(
                [MarshalAs(UnmanagedType.LPArray)] long[] ids, int count);

            [DllImport(OrionDll)]
            public static extern void SetupLogging();

            [DllImport(OrionDll)]
            public static extern void OrionLog(byte level, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

            [DllImport(OrionDll)]
            public static extern IntPtr GetBookmarkFolders(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeBookmarkFolders(IntPtr folders);

            [DllImport(OrionDll)]
            public static extern IntPtr GetBookmarks(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern IntPtr GetRecentBookmarks(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeBookmarks(IntPtr bookmarks);

            [DllImport(OrionDll)]
            public static extern void MoveBookmark(long id, long parentFolderId, long nextBookmarkId, long nextFolderId);

            [DllImport(OrionDll)]
            public static extern void MoveBookmarkFolder(long id, long parentFolderId, long nextBookmarkId, long nextFolderId);

            [DllImport(OrionDll)]
            public static extern long CreateBookmarkFolder(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                long parentFolderId);

            [DllImport(OrionDll)]
            public static extern long CreateBookmark(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string address,
                long parentFolderId);

            [DllImport(OrionDll)]
            public static extern void DeleteBookmark(long id);

            [DllImport(OrionDll)]
            public static extern void DeleteBookmarkFolder(long id);

            [DllImport(OrionDll)]
            public static extern void SetDefaultProfile(long profileId);

            [DllImport(OrionDll)]
            public static extern IntPtr GetProfiles(out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeProfiles(IntPtr profiles);

            [DllImport(OrionDll)]
            public static extern long CreateProfile(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string engine);

            [DllImport(OrionDll)]
            public static extern void UpdateProfile(
                long id,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
                sbyte showInAddressBar,
                sbyte showBadgeInAppIcon,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string keyboardShortcut,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string engine);

            [DllImport(OrionDll)]
            public static extern long GetCurrentProfileId();

            [DllImport(OrionDll)]
            public static extern long ResolveStartupProfile(long requestedId);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string GetAppSetting(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultValue);

            [DllImport(OrionDll)]
            public static extern void SetAppSetting(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string type);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool DeleteProfile(long id);

            [DllImport(OrionDll)]
            public static extern void DeleteProfileData(long id);

            [DllImport(OrionDll)]
            public static extern IntPtr GetDeletedProfileIds(out int outCount);

            [DllImport(OrionDll, EntryPoint = "MoveProfileEntry")]
            public static extern void MoveProfile(long profileId, long nextProfileId);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string GetSetting(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string realm,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string keyName,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultValue);

            [DllImport(OrionDll)]
            public static extern void SetSetting(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string realm,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string keyName,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultValue);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string GetSettingForWebsite(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string website,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string keyName,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultValue);

            [DllImport(OrionDll)]
            public static extern void SetSettingForWebsite(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string website,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string keyName,
                long value, long defaultValue);

            [DllImport(OrionDll)]
            public static extern long AddSettingObserver(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
                SettingChangedCallback callback);

            [DllImport(OrionDll)]
            public static extern void RemoveSettingObserver(long observerId);

            // Notifications
            [DllImport(OrionDll)]
            public static extern void SetNotificationCallbacks(
                ShowNotificationCallback? showCb, ClearNotificationCallback? clearCb);

            [DllImport(OrionDll)]
            public static extern void NotifyNotificationEvent(
                int type, int source,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string notificationId,
                int buttonIndex, int byUser);

            [DllImport(OrionDll)]
            public static extern IntPtr GetReadingList(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeReadingList(IntPtr entries);

            [DllImport(OrionDll)]
            public static extern long AddReadingListEntry(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string address);

            [DllImport(OrionDll)]
            public static extern void DeleteReadingListEntries(
                [MarshalAs(UnmanagedType.LPArray)] long[] ids, int count);

            [DllImport(OrionDll)]
            public static extern void MarkReadingListEntryRead(long id, sbyte isUnread);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string InstallExtension(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string displayName,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string info,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string installURL,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string webExtType,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string webExtID,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string geckoId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string iconURL,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string version,
                sbyte orionHosted,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? filePath);

            [DllImport(OrionDll)]
            public static extern IntPtr GetInstalledExtensions(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeInstalledExtensions(IntPtr extensions);

            [DllImport(OrionDll)]
            public static extern IntPtr GetExtensionToolbarItems(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeExtensionToolbarItems(IntPtr actions);

            [DllImport(OrionDll)]
            public static extern void UninstallExtension(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string extensionId);

            [DllImport(OrionDll)]
            public static extern void SetExtensionEnabled(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string extensionId, sbyte enabled);

            [DllImport(OrionDll)]
            public static extern void ConfigureWebViewExtensions(Win32.HWND hwnd, long profileId);

            // Windows
            [DllImport(OrionDll)]
            public static extern IntPtr GetWindows(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeWindows(IntPtr windows);

            [DllImport(OrionDll, EntryPoint = "MoveWindowEntry")]
            public static extern void MoveWindow(long windowId, long nextWindowId);

            [DllImport(OrionDll)]
            public static extern long GetSharedPinnedTabsWindowId(long profileId);

            [DllImport(OrionDll, EntryPoint = "CreateOrionWindowEntry")]
            public static extern long CreateWindow(long profileId, sbyte isTabGroup, sbyte hasUiWindow);

            [DllImport(OrionDll)]
            public static extern void UpdateWindowSelectedTab(long windowId, long selectedTabId);

            [DllImport(OrionDll)]
            public static extern void UpdateWindowHasUiWindow(long windowId, sbyte hasUiWindow);

            [DllImport(OrionDll)]
            public static extern void UpdateWindowIsTabGroup(long windowId, sbyte isTabGroup);

            [DllImport(OrionDll)]
            public static extern void UpdateWindowName(long windowId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport(OrionDll)]
            public static extern void UpdateWindowKeyboardShortcut(long windowId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string shortcut);

            [DllImport(OrionDll, EntryPoint = "CloseOrionWindowEntry")]
            public static extern void CloseWindow(long windowId);

            // Tabs
            [DllImport(OrionDll)]
            public static extern IntPtr GetTabs(long windowId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeTabs(IntPtr tabs);

            [DllImport(OrionDll)]
            public static extern long CreateTab(
                long windowId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string uri,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                sbyte isPinned, long parentTabId);

            [DllImport(OrionDll)]
            public static extern void UpdateTabUri(long tabId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);

            [DllImport(OrionDll)]
            public static extern void UpdateTabName(long tabId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport(OrionDll)]
            public static extern sbyte SaveTabPinState(long tabId, long windowId, sbyte updatePin,
                sbyte isPinned, sbyte moveToWindow,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string uri,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport(OrionDll)]
            public static extern sbyte ReorderPinnedTab(long tabId, long windowId, long beforeTabId);

            [DllImport(OrionDll)]
            public static extern void UpdateTabWindowId(long tabId, long windowId);

            [DllImport(OrionDll)]
            public static extern void MoveTab(long tabId, long targetWindowId, long parentTabId, long afterTabId, sbyte isNested);

            [DllImport(OrionDll)]
            public static extern void DeleteTabs(
                [MarshalAs(UnmanagedType.LPArray)] long[] ids, int count);

            // Recently Closed Tabs
            [DllImport(OrionDll)]
            public static extern void CreateRecentlyClosedTab(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);

            [DllImport(OrionDll)]
            public static extern IntPtr GetRecentlyClosedTabs(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeRecentlyClosedTabs(IntPtr tabs);

            [DllImport(OrionDll)]
            public static extern void DeleteRecentlyClosedTabs(
                [MarshalAs(UnmanagedType.LPArray)] long[] ids, int count);

            // Undo/Redo Tabs
            [DllImport(OrionDll)]
            public static extern void CreateUndoRedoTabStep(
                long windowId, [MarshalAs(UnmanagedType.LPArray)] long[] tabIds, int count);

            [DllImport(OrionDll)]
            public static extern void ChangeUndoRedoTabStep(long windowId, sbyte isUndo);

            [DllImport(OrionDll)]
            public static extern void IsUndoRedoTabStepAvailable(
                long windowId, out sbyte outUndo, out sbyte outRedo);

            // Recently Closed Windows
            [DllImport(OrionDll)]
            public static extern IntPtr GetRecentlyClosedWindows(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeRecentlyClosedWindows(IntPtr windows);

            [DllImport(OrionDll)]
            public static extern void DeleteRecentlyClosedWindows(
                [MarshalAs(UnmanagedType.LPArray)] long[] ids, int count);

            // Favicons
            [DllImport(OrionDll)]
            public static extern void SetFavicon(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string pageUrl,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string iconUrl,
                [MarshalAs(UnmanagedType.LPArray)] byte[] data, int dataSize);

            [DllImport(OrionDll)]
            public static extern IntPtr GetFavicon(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string pageUrl,
                out int outSize, out sbyte outExpired,
                [MarshalAs(UnmanagedType.LPWStr)] out string? outIconUrl);

            [DllImport(OrionDll)]
            public static extern void FreeFavicon(IntPtr data);

            [DllImport(OrionDll)]
            public static extern IntPtr GetPasswordItemIds(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string searchText,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string authority,
                out int count);

            [DllImport(OrionDll)]
            public static extern void FreePasswordItemIds(IntPtr ids);

            [DllImport(OrionDll)]
            public static extern IntPtr GetPasswordItemsByIds(
                [In] long[] ids,
                int count,
                out int outCount);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.LPUTF8Str)]
            public static extern string ExportPasswordItemsCsv(
                [In] long[] ids,
                int count);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ImportPasswordItemsFromCsv(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string csvText,
                out int importedCount,
                out int notImportedCount);

            [DllImport(OrionDll)]
            public static extern long CreatePasswordItem(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string verificationKey,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string verificationCode,
                [MarshalAs(UnmanagedType.Bool)] bool updateExisting);

            [DllImport(OrionDll)]
            public static extern void UpdatePasswordItem(
                long id,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string verificationKey,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string verificationCode);

            [DllImport(OrionDll)]
            public static extern void DeletePasswordItems(
                [In] long[] ids,
                int count);

            [DllImport(OrionDll)]
            public static extern void FreePasswordItems(IntPtr items, int count);

            [DllImport(OrionDll)]
            public static extern long CreateNeverSavePasswordItem(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string url);

            [DllImport(OrionDll)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool GetNeverSavePasswordItem(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string authority);

            [DllImport(OrionDll)]
            public static extern void CreateSnapshot(long profileId);

            [DllImport(OrionDll)]
            public static extern long GetLastSnapshotTimestamp(long profileId);

            [DllImport(OrionDll)]
            public static extern long RestoreSnapshot(long profileId, long createdAt);

            [DllImport(OrionDll)]
            public static extern void ExportProfileData(long profileId,
                [MarshalAs(UnmanagedType.LPWStr)] string directory,
                SyncProgressCallback? progressCb, SyncCompletionCallback completionCb);

            [DllImport(OrionDll)]
            public static extern void ImportProfileData(long profileId,
                [MarshalAs(UnmanagedType.LPWStr)] string source,
                SyncProgressCallback? progressCb, SyncCompletionCallback completionCb);

            [DllImport(OrionDll)]
            public static extern void CancelSync();

            // Downloads
            [DllImport(OrionDll)]
            public static extern void SetDownloadCallbacks(
                DownloadChangedCallback? createdCb,
                DownloadChangedCallback? updatedCb,
                DownloadDeletedCallback? deletedCb);

            [DllImport(OrionDll)]
            public static extern IntPtr GetDownloads(long profileId, out int outCount);

            [DllImport(OrionDll)]
            public static extern void FreeDownloads(IntPtr entries);

            [DllImport(OrionDll)]
            public static extern void DeleteDownloads(
                [MarshalAs(UnmanagedType.LPArray)] long[] ids, int count);

            [DllImport(OrionDll)]
            public static extern long CreateDownload(
                long profileId,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                int operationState, long totalSize, long currentSize,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string originUri);

            [DllImport(OrionDll)]
            public static extern void UpdateDownload(
                long downloadId, int operationState,
                long totalSize, long currentSize,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? path);

            [DllImport(OrionDll)]
            public static extern void CancelDownload(long downloadId);

            [DllImport(OrionDll)]
            public static extern void RestartDownload(long downloadId);
        }

        [DllImport(OrionDll)]
        public static extern HRESULT LoadURL(Win32.HWND hwnd, [MarshalAs(UnmanagedType.LPUTF8Str)] string url);

        [DllImport(OrionDll)]
        public static extern void LoadHTMLString(Win32.HWND hwnd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string html,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string unreachableUrl);

        [DllImport(OrionDll)]
        public static extern void ExecuteJavaScript(Win32.HWND hwnd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string script);

        [DllImport(OrionDll)]
        public static extern void Reload(Win32.HWND hwnd);

        [DllImport(OrionDll)]
        public static extern void ReloadBypassingCache(Win32.HWND hwnd);

        [DllImport(OrionDll)]
        public static extern void Forward(Win32.HWND hwnd);

        [DllImport(OrionDll)]
        public static extern void Backward(Win32.HWND hwnd);

        [DllImport(OrionDll)]
        public static extern void SetCustomUserAgent(Win32.HWND hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? userAgent);

        // Process-default user agent from the profile's setting, covering
        // pages never reached per-view (window.open popups, loads racing
        // SetCustomUserAgent). Call after LoadBrowserEngine, before the
        // first view: CEF latches it at engine initialization; WebKit
        // applies it to every page at creation.
        [DllImport(OrionDll)]
        public static extern void SetDefaultUserAgent([MarshalAs(UnmanagedType.LPWStr)] string userAgent);

        /// <summary>
        /// Minimum page font size in px; 0 = no minimum. Applies to all
        /// profiles. WebKit applies to open pages immediately; CEF only to
        /// views created afterwards (new tabs).
        /// </summary>
        [DllImport(OrionDll)]
        public static extern void SetMinimumFontSize(int size);

        /// <summary>
        /// JavaScript on/off for all websites. Applies to all profiles; takes
        /// effect from the next navigation (running scripts are unaffected).
        /// </summary>
        [DllImport(OrionDll)]
        public static extern void SetJavaScriptEnabled(int enabled);

        /// <summary>
        /// Engine-native popup blocking (Chromium's built-in blocker; no-op
        /// on WebKit). Disabled whenever the app's popup setting owns the
        /// policy, so every window.open reaches the popup-create gate.
        /// </summary>
        [DllImport(OrionDll)]
        public static extern void SetNativePopupBlocking(int enabled);

        [DllImport(OrionDll)]
        public static extern void ExecuteContextMenuCommand(Win32.HWND hwnd, WebContextMenuCommand command,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? arg, int x, int y);

        // Opens (or focuses) the view's DevTools frontend; the frontend view
        // arrives via BrowserCallbacks.DevToolsViewChanged. x/y >= 0 inspect
        // the element at those view coordinates (CEF only).
        [DllImport(OrionDll)]
        public static extern void ShowDevTools(Win32.HWND hwnd, int x, int y);

        // Closes the view's DevTools frontend; DevToolsViewChanged(zero) follows.
        [DllImport(OrionDll)]
        public static extern void CloseDevTools(Win32.HWND hwnd);

        // findNext=false starts/restarts a search for text; true advances from
        // the current match. Results arrive via BrowserCallbacks.FindResult.
        // text must be non-empty (call StopFindInPage instead).
        [DllImport(OrionDll)]
        public static extern void FindInPage(Win32.HWND hwnd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
            [MarshalAs(UnmanagedType.Bool)] bool forward,
            [MarshalAs(UnmanagedType.Bool)] bool findNext);

        // Ends the find session and hides match highlights.
        [DllImport(OrionDll)]
        public static extern void StopFindInPage(Win32.HWND hwnd,
            [MarshalAs(UnmanagedType.Bool)] bool clearSelection);

        // Requests an async snapshot of the view's visible viewport (JPEG,
        // at most maxPixelWidth wide; 0 = native size). The result arrives
        // via BrowserCallbacks.SnapshotTaken. Only reliable while the view
        // is (or was just) visible — hidden views can't paint.
        [DllImport(OrionDll)]
        public static extern void RequestTabSnapshot(Win32.HWND hwnd, int maxPixelWidth);

        // Resolves a media-access request raised via
        // BrowserCallbacks.MediaAccessPermissionRequested. allow grants
        // exactly what the page requested; ids whose view is already gone
        // are ignored.
        [DllImport(OrionDll)]
        public static extern void RespondToMediaAccessPermissionRequest(long requestId,
            [MarshalAs(UnmanagedType.Bool)] bool allow);

        // Resolves an HTTP-auth challenge raised via
        // BrowserCallbacks.AuthCredentialsRequested. allow=0 cancels (the
        // site's 401 body renders); ids whose view is already gone are ignored.
        [DllImport(OrionDll)]
        public static extern void RespondToAuthCredentials(long requestId, int allow,
            [MarshalAs(UnmanagedType.LPWStr)] string? username,
            [MarshalAs(UnmanagedType.LPWStr)] string? password);

        // Pushes the effective UI appearance to the engine (drives CSS
        // prefers-color-scheme). Safe before LoadBrowserEngine: the backend
        // latches the value and re-applies it when the engine loads.
        [DllImport(OrionDll)]
        public static extern void SetUseDarkAppearance([MarshalAs(UnmanagedType.Bool)] bool useDarkAppearance);



        public static void ShutdownOrionCore() => Dll.ShutdownOrionCore();



        // Pumps the orion run loop once on the UI thread.
        // Returns true while running, false once fully stopped — do not call again after false.
        public static bool TickOrionCore() => Dll.TickOrionCore() != 0;

        // Returns the Win32 auto-reset event HANDLE signalled whenever the run loop
        // has work pending. Pass to WaitForSingleObject on a background thread, then
        // call TickOrionCore() on the UI thread when it fires.
        public static Win32.HANDLE GetWakeupEventHandle() => Dll.GetWakeupEventHandle();

        public static Win32.HANDLE GetCefWakeupEventHandle() => Dll.GetCefWakeupEventHandle();

        public static void PumpCefMessageLoop() => Dll.PumpCefMessageLoop();

        /// <summary>
        /// Returns -1 if this is the main browser process (continue normal startup).
        /// Returns >= 0 if this is an engine subprocess (exit with this code).
        /// Must be called before Application.Start().
        /// </summary>
        public static int CheckBrowserSubprocess() => Dll.CheckBrowserSubprocess();

        public static string GetOrionVersion() => Dll.GetOrionVersion();

        /// <summary>
        ///  Returns "OK" on success, or an error message on failure. Must be called before creating any windows.
        /// </summary>
        /// <summary>
        /// Initializes the core against the given profile's own database
        /// (data\Profiles\&lt;id&gt;\profile.db), splitting the legacy single DB or
        /// provisioning a fresh profile DB when needed. profileId 0 = the
        /// registry's default profile.
        /// </summary>
        public static string InitOrionCore(long profileId = 0) => Dll.InitOrionCore(profileId);

        /// <summary>
        /// True once LoadBrowserEngine has loaded the CEF/Blink engine.
        /// The engine is fixed for the process lifetime.
        /// </summary>
        public static bool IsBlinkEngine { get; private set; }

        /// <summary>
        /// Loads the engine plugin ("webkit" or "blink"). Call after
        /// InitOrionCore and before the first CreateOrionWindow; throws if the
        /// plugin fails to load or a different engine is already loaded.
        /// </summary>
        public static void LoadBrowserEngine(string engine)
        {
            string result = Dll.LoadBrowserEngine(engine);
            if (result != "OK")
                throw new InvalidOperationException($"Failed to load browser engine '{engine}': {result}");
            IsBlinkEngine = Dll.GetBrowserEngine() == 1;
        }

        public static Win32.HWND CreateOrionWindow(Win32.HWND parentWindow, BrowserCallbacks browserCallbacks, long profileId)
        {
            return Dll.CreateOrionWindow(parentWindow, browserCallbacks, profileId);
        }

        public static Win32.HWND CreateOrionWindowWithUrl(Win32.HWND parentWindow, BrowserCallbacks browserCallbacks, long profileId, string url)
        {
            return Dll.CreateOrionWindowWithUrl(parentWindow, browserCallbacks, profileId, url);
        }

        // Holds strong references so the GC doesn't collect the delegates
        // while native code still holds their function pointers.
#pragma warning disable IDE0052 // Remove unread private members — read by GC, not by code
        private static CreatePopupCallback? s_createPopupCallback;
        private static PopupReadyCallback? s_popupReadyCallback;
        private static OpenURLCallback? s_openURLCallback;
#pragma warning restore IDE0052

        public static void SetCreatePopupCallback(CreatePopupCallback cb)
        {
            s_createPopupCallback = cb;
            Dll.SetCreatePopupCallback(cb);
        }

        public static void SetPopupReadyCallback(PopupReadyCallback cb)
        {
            s_popupReadyCallback = cb;
            Dll.SetPopupReadyCallback(cb);
        }

        public static void SetOpenURLCallback(OpenURLCallback cb)
        {
            s_openURLCallback = cb;
            Dll.SetOpenURLCallback(cb);
        }

        public static void ResizeOrionWindow(Win32.HWND hwnd, int width, int height) => Dll.ResizeOrionWindow(hwnd, width, height);

        public static void RequestExitElementFullscreen(Win32.HWND hwnd) => Dll.RequestExitElementFullscreen(hwnd);

        public static void DestroyOrionWindow(Win32.HWND hwnd) => Dll.DestroyOrionWindow(hwnd);

        internal static bool IsPrivateSession { get; private set; }

        internal static bool ConfigurePrivateSession() => IsPrivateSession = Dll.ConfigurePrivateSession();

        public static void SetupLogging() => Dll.SetupLogging();

        public static void LogInfo(string message) => Dll.OrionLog((byte)'I', message);

        public static void LogError(string message) => Dll.OrionLog((byte)'E', message);

        public static void LogWarning(string message) => Dll.OrionLog((byte)'W', message);

        public static void LogDebug(string message) => Dll.OrionLog((byte)'D', message);

        public static void AddHistoryEntry(long profileId, string url, string title)
        {
            Dll.AddHistoryEntry(profileId, url, title);
        }

        public static List<HistoryEntry> GetHistoryForMenu(long profileId, int days = 4)
        {
            IntPtr ptr = Dll.GetHistoryForMenu(profileId, days, out int count);
            var result = new List<HistoryEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<HistoryEntryNative>();
            for (int i = 0; i < count; i++)
            {
                HistoryEntryNative native = Marshal.PtrToStructure<HistoryEntryNative>(ptr + (i * structSize));
                result.Add(new HistoryEntry
                {
                    Id = new HistoryEntryId(native.id),
                    ProfileId = new ProfileId(native.profile_id),
                    VisitedAt = DateTimeOffset.FromUnixTimeSeconds(native.visited_at).LocalDateTime,
                    Website = Marshal.PtrToStringUTF8(native.website) ?? "",
                    Address = Marshal.PtrToStringUTF8(native.address) ?? "",
                });
            }
            Dll.FreeHistoryEntries(ptr);
            return result;
        }

        public static void ClearHistory(long profileId) => Dll.ClearHistory(profileId);

        public static void DeleteHistoryEntries(long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeleteHistoryEntries(ids, ids.Length);
        }

        public static List<HistoryEntry> GetRecentHistory(long profileId)
        {
            IntPtr ptr = Dll.GetRecentHistory(profileId, out int count);
            var result = new List<HistoryEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<HistoryEntryNative>();
            for (int i = 0; i < count; i++)
            {
                HistoryEntryNative native = Marshal.PtrToStructure<HistoryEntryNative>(ptr + (i * structSize));
                result.Add(new HistoryEntry
                {
                    Id = new HistoryEntryId(native.id),
                    ProfileId = new ProfileId(native.profile_id),
                    VisitedAt = DateTimeOffset.FromUnixTimeSeconds(native.visited_at).LocalDateTime,
                    Website = Marshal.PtrToStringUTF8(native.website) ?? "",
                    Address = Marshal.PtrToStringUTF8(native.address) ?? "",
                });
            }
            Dll.FreeHistoryEntries(ptr);
            return result;
        }

        public static List<BookmarkEntry> GetBookmarkFolders(long profileId)
        {
            IntPtr ptr = Dll.GetBookmarkFolders(profileId, out int count);
            var result = new List<BookmarkEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<BookmarkFolderNative>();
            for (int i = 0; i < count; i++)
            {
                BookmarkFolderNative native = Marshal.PtrToStructure<BookmarkFolderNative>(ptr + (i * structSize));
                result.Add(new BookmarkEntry
                {
                    Id = native.id,
                    Title = Marshal.PtrToStringUTF8(native.name) ?? "",
                    ParentFolderId = native.parent_folder_id,
                    IsFolder = true,
                    IsInternal = native.is_internal != 0,
                    FolderType = native.type,
                });
            }
            Dll.FreeBookmarkFolders(ptr);
            return result;
        }

        public static List<BookmarkEntry> GetBookmarks(long profileId)
        {
            IntPtr ptr = Dll.GetBookmarks(profileId, out int count);
            var result = new List<BookmarkEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<BookmarkNative>();
            for (int i = 0; i < count; i++)
            {
                BookmarkNative native = Marshal.PtrToStructure<BookmarkNative>(ptr + (i * structSize));
                result.Add(new BookmarkEntry
                {
                    Id = native.id,
                    Title = Marshal.PtrToStringUTF8(native.name) ?? "",
                    Url = Marshal.PtrToStringUTF8(native.address) ?? "",
                    ParentFolderId = native.parent_folder_id,
                    IsFolder = false,
                });
            }
            Dll.FreeBookmarks(ptr);
            return result;
        }

        public static List<BookmarkEntry> GetRecentBookmarks(long profileId)
        {
            IntPtr ptr = Dll.GetRecentBookmarks(profileId, out int count);
            var result = new List<BookmarkEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<BookmarkNative>();
            for (int i = 0; i < count; i++)
            {
                BookmarkNative native = Marshal.PtrToStructure<BookmarkNative>(ptr + (i * structSize));
                result.Add(new BookmarkEntry
                {
                    Id = native.id,
                    Title = Marshal.PtrToStringUTF8(native.name) ?? "",
                    Url = Marshal.PtrToStringUTF8(native.address) ?? "",
                    ParentFolderId = native.parent_folder_id,
                    IsFolder = false,
                });
            }
            Dll.FreeBookmarks(ptr);
            return result;
        }

        public static void MoveBookmark(long id, long parentFolderId, long nextBookmarkId = 0, long nextFolderId = 0)
        {
            Dll.MoveBookmark(id, parentFolderId, nextBookmarkId, nextFolderId);
        }

        public static void MoveBookmarkFolder(long id, long parentFolderId, long nextBookmarkId = 0, long nextFolderId = 0)
        {
            Dll.MoveBookmarkFolder(id, parentFolderId, nextBookmarkId, nextFolderId);
        }

        // Returns the new folder's ID, or 0 on failure.
        public static long CreateBookmarkFolder(long profileId, string name, long parentFolderId = 0)
        {
            return Dll.CreateBookmarkFolder(profileId, name, parentFolderId);
        }

        // Returns the new bookmark's ID, or 0 on failure.
        public static long CreateBookmark(long profileId, string name, string address, long parentFolderId = 0)
        {
            return Dll.CreateBookmark(profileId, name, address, parentFolderId);
        }

        public static void DeleteBookmark(long id) => Dll.DeleteBookmark(id);

        public static void DeleteBookmarkFolder(long id) => Dll.DeleteBookmarkFolder(id);

        public static void SetDefaultProfile(long profileId)
        {
            Dll.SetDefaultProfile(profileId);
            SingleInstance.BroadcastProfilesChanged();
        }

        public static List<Profile> GetProfiles()
        {
            IntPtr ptr = Dll.GetProfiles(out int count);
            var result = new List<Profile>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<ProfileNative>();
            for (int i = 0; i < count; i++)
            {
                ProfileNative native = Marshal.PtrToStructure<ProfileNative>(ptr + (i * structSize));
                result.Add(new Profile
                {
                    Id = native.id,
                    Name = Marshal.PtrToStringUTF8(native.name) ?? "",
                    Icon = Marshal.PtrToStringUTF8(native.icon_name) ?? "",
                    ShowInAddressBar = native.show_in_address_bar != 0,
                    ShowBadgeInAppIcon = native.show_badge_in_app_icon != 0,
                    KeyboardShortcut = Marshal.PtrToStringUTF8(native.keyboard_shortcut) ?? "",
                    IsInternal = native.is_internal != 0,
                    IsDefault = native.is_default != 0,
                    Engine = Marshal.PtrToStringUTF8(native.engine) ?? "webkit",
                });
            }
            Dll.FreeProfiles(ptr);
            return result;
        }

        // Registry mutations broadcast profiles-changed to the other profiles'
        // processes so their profile UIs refresh (mirrors macOS
        // OrionProfilesDidUpdate).
        public static long CreateProfile(string name, string iconName, string engine = "webkit")
        {
            long id = Dll.CreateProfile(name, iconName, engine);
            if (id != 0)
                SingleInstance.BroadcastProfilesChanged();
            return id;
        }

        public static void UpdateProfile(Profile profile)
        {
            Dll.UpdateProfile(
                profile.Id,
                profile.Name,
                profile.Icon,
                (sbyte)(profile.ShowInAddressBar ? 1 : 0),
                (sbyte)(profile.ShowBadgeInAppIcon ? 1 : 0),
                profile.KeyboardShortcut,
                profile.Engine);
            SingleInstance.BroadcastProfilesChanged();
        }

        /// <summary>Returns false when deletion was refused (last profile).</summary>
        public static bool DeleteProfile(long id)
        {
            bool deleted = Dll.DeleteProfile(id);
            if (deleted)
                SingleInstance.BroadcastProfilesChanged();
            return deleted;
        }

        /// <summary>
        /// Removes a deleted profile's data dirs. Call only from a process
        /// that is not (and never was) that profile's own — see
        /// ProfileDataCleanup.
        /// </summary>
        public static void DeleteProfileData(long id) => Dll.DeleteProfileData(id);

        /// <summary>
        /// Ids of deleted profiles (their registry rows are kept forever so
        /// ids are never reused); their data dirs may still await deletion.
        /// </summary>
        public static long[] GetDeletedProfileIds()
        {
            IntPtr ptr = Dll.GetDeletedProfileIds(out int count);
            if (ptr == IntPtr.Zero || count == 0)
                return [];
            long[] ids = new long[count];
            Marshal.Copy(ptr, ids, 0, count);
            Marshal.FreeCoTaskMem(ptr);
            return ids;
        }

        public static void MoveProfile(long profileId, long nextProfileId)
        {
            Dll.MoveProfile(profileId, nextProfileId);
            SingleInstance.BroadcastProfilesChanged();
        }

        /// <summary>Registry id of the profile this process hosts (0 before InitOrionCore).</summary>
        public static long GetCurrentProfileId() => Dll.GetCurrentProfileId();

        /// <summary>
        /// Resolves which profile this process should host (0 = default),
        /// running the first-launch legacy split / registry seeding when
        /// needed. Returns 0 on failure. Safe before InitOrionCore.
        /// </summary>
        public static long ResolveStartupProfile(long requestedId) => Dll.ResolveStartupProfile(requestedId);

        // App-wide settings shared by every profile (common orion.db) —
        // distinct from the per-profile Get/SetSetting above.
        public static string GetAppSetting(string key, string defaultValue)
            => Dll.GetAppSetting(key, defaultValue);

        public static void SetAppSetting(string key, string value, string type = "text")
            => Dll.SetAppSetting(key, value, type);

        // Generic setting access. type: "text" | "integer" | "boolean" | "real"
        public static string GetSetting(long profileId, string key, string realm, string keyName, string type, string defaultValue)
        {
            return Dll.GetSetting(profileId, key, realm, keyName, type, defaultValue);
        }

        public static void SetSetting(long profileId, string key, string realm, string keyName, string type, string value, string defaultValue)
        {
            Dll.SetSetting(profileId, key, realm, keyName, type, value, defaultValue);
        }

        // Per-website setting (settings::website::specific::<website>::<keyName>),
        // falling back to the profile's corresponding all-websites default.
        // website is a page URL's authority; values keep their raw stored
        // representation (integers as decimal strings).
        public static string GetSettingForWebsite(long profileId, string website, string keyName, string defaultValue)
            => Dll.GetSettingForWebsite(profileId, website, keyName, defaultValue);

        public static void SetSettingForWebsite(long profileId, string website, string keyName, long value, long defaultValue)
            => Dll.SetSettingForWebsite(profileId, website, keyName, value, defaultValue);

        // Typed convenience helpers — booleans stored as "1"/"0", enums/ints as decimal.
        public static string GetStringSetting(long profileId, string key, string realm, string keyName, string defaultValue)
        {
            return GetSetting(profileId, key, realm, keyName, "text", defaultValue);
        }

        public static void SetStringSetting(long profileId, string key, string realm, string keyName, string value, string defaultValue)
        {
            SetSetting(profileId, key, realm, keyName, "text", value, defaultValue);
        }

        public static bool GetBoolSetting(long profileId, string key, string realm, string keyName, bool defaultValue)
        {
            string raw = GetSetting(profileId, key, realm, keyName, "boolean", defaultValue ? "1" : "0");
            return raw == "1";
        }

        public static void SetBoolSetting(long profileId, string key, string realm, string keyName, bool value, bool defaultValue)
        {
            SetSetting(profileId, key, realm, keyName, "boolean", value ? "1" : "0", defaultValue ? "1" : "0");
        }

        public static int GetIntSetting(long profileId, string key, string realm, string keyName, int defaultValue)
        {
            string raw = GetSetting(profileId, key, realm, keyName, "integer", defaultValue.ToString());
            return int.TryParse(raw, out int result) ? result : defaultValue;
        }

        /// <summary>Like GetBoolSetting, but null when the setting was never stored.</summary>
        public static bool? GetBoolSettingOrNull(long profileId, string key, string realm, string keyName)
        {
            string raw = GetSetting(profileId, key, realm, keyName, "boolean", "");
            return raw.Length == 0 ? null : raw == "1";
        }

        /// <summary>Like GetIntSetting, but null when the setting was never stored.</summary>
        public static int? GetIntSettingOrNull(long profileId, string key, string realm, string keyName)
        {
            string raw = GetSetting(profileId, key, realm, keyName, "integer", "");
            return int.TryParse(raw, out int result) ? result : null;
        }

        public static void SetIntSetting(long profileId, string key, string realm, string keyName, int value, int defaultValue)
        {
            SetSetting(profileId, key, realm, keyName, "integer", value.ToString(), defaultValue.ToString());
        }

        /// <summary>
        /// Observes changes to a single setting key across all profiles. The callback
        /// fires on the UI thread after each committed write. The caller must keep a
        /// reference to the delegate while the observer is registered, and remove the
        /// observer with RemoveSettingObserver when done.
        /// </summary>
        public static long AddSettingObserver(string key, SettingChangedCallback callback) =>
            Dll.AddSettingObserver(key, callback);

        public static void RemoveSettingObserver(long observerId) => Dll.RemoveSettingObserver(observerId);

        public static List<ReadingListEntry> GetReadingList(long profileId)
        {
            IntPtr ptr = Dll.GetReadingList(profileId, out int count);
            var result = new List<ReadingListEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<ReadingListEntryNative>();
            for (int i = 0; i < count; i++)
            {
                ReadingListEntryNative native = Marshal.PtrToStructure<ReadingListEntryNative>(ptr + (i * structSize));
                result.Add(new ReadingListEntry
                {
                    Id = new ReadingListEntryId(native.id),
                    ProfileId = new ProfileId(native.profile_id),
                    AddedOn = DateTimeOffset.FromUnixTimeSeconds(native.added_on).LocalDateTime,
                    IconName = Marshal.PtrToStringUTF8(native.icon_name) ?? "",
                    Title = Marshal.PtrToStringUTF8(native.title) ?? "",
                    Address = Marshal.PtrToStringUTF8(native.address) ?? "",
                    Referrer = Marshal.PtrToStringUTF8(native.referrer) ?? "",
                    IsUnread = native.is_unread != 0,
                    IsSavedOffline = native.is_saved_offline != 0,
                    Description = Marshal.PtrToStringUTF8(native.description) ?? "",
                });
            }
            Dll.FreeReadingList(ptr);
            return result;
        }

        public static long AddReadingListEntry(long profileId, string title, string address)
        {
            return Dll.AddReadingListEntry(profileId, title, address);
        }

        public static void DeleteReadingListEntries(long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeleteReadingListEntries(ids, ids.Length);
        }

        public static void MarkReadingListEntryRead(long id, bool isUnread)
        {
            Dll.MarkReadingListEntryRead(id, (sbyte)(isUnread ? 1 : 0));
        }

        public static string InstallExtension(
            long profileId,
            string name, string displayName, string info, string installURL,
            string webExtType, string webExtID, string geckoId, string iconURL,
            string version, bool orionHosted, string? filePath = null)
        {
            string resultId = Dll.InstallExtension(
                profileId, name, displayName, info, installURL, webExtType,
                webExtID, geckoId, iconURL, version,
                (sbyte)(orionHosted ? 1 : 0), filePath);
            if (!string.IsNullOrEmpty(resultId))
                ExtensionsChanged.Notify();
            return resultId;
        }

        public static List<ExtensionEntry> GetInstalledExtensions(long profileId)
        {
            IntPtr ptr = Dll.GetInstalledExtensions(profileId, out int count);
            var result = new List<ExtensionEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<InstalledExtensionNative>();
            for (int i = 0; i < count; i++)
            {
                InstalledExtensionNative native = Marshal.PtrToStructure<InstalledExtensionNative>(ptr + (i * structSize));
                result.Add(new ExtensionEntry
                {
                    Id = Marshal.PtrToStringUTF8(native.id) ?? "",
                    Name = Marshal.PtrToStringUTF8(native.name) ?? "",
                    Version = Marshal.PtrToStringUTF8(native.version) ?? "",
                    ExtensionType = native.type,
                    IsEnabled = native.is_enabled != 0,
                });
            }
            Dll.FreeInstalledExtensions(ptr);
            return result;
        }

        public static List<ExtensionToolbarItem> GetExtensionToolbarItems(long profileId)
        {
            IntPtr ptr = Dll.GetExtensionToolbarItems(profileId, out int count);
            var result = new List<ExtensionToolbarItem>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<ExtensionToolbarItemNative>();
            for (int i = 0; i < count; i++)
            {
                ExtensionToolbarItemNative native = Marshal.PtrToStructure<ExtensionToolbarItemNative>(ptr + (i * structSize));
                byte[]? iconData = null;
                if (native.icon_data != IntPtr.Zero && native.icon_data_len > 0)
                {
                    iconData = new byte[native.icon_data_len];
                    Marshal.Copy(native.icon_data, iconData, 0, native.icon_data_len);
                }
                result.Add(new ExtensionToolbarItem
                {
                    Id = Marshal.PtrToStringUTF8(native.id) ?? "",
                    Title = Marshal.PtrToStringUTF8(native.title) ?? "",
                    PopupUri = Marshal.PtrToStringUTF8(native.popup_uri) ?? "",
                    IconData = iconData,
                });
            }
            Dll.FreeExtensionToolbarItems(ptr);
            return result;
        }

        public static void UninstallExtension(string extensionId)
        {
            Dll.UninstallExtension(extensionId);
            ExtensionsChanged.Notify();
        }

        public static void SetExtensionEnabled(string extensionId, bool enabled)
        {
            Dll.SetExtensionEnabled(extensionId, (sbyte)(enabled ? 1 : 0));
            ExtensionsChanged.Notify();
        }

        public static void ConfigureWebViewExtensions(Win32.HWND hwnd, long profileId)
        {
            Dll.ConfigureWebViewExtensions(hwnd, profileId);
        }

        // ---------------------------------------------------------------
        // Tab Groups (backed by core module "windows" table)
        // ---------------------------------------------------------------

        public record PersistedTabGroup(long Id, long ProfileId, string Name,
            bool IsTabGroup, long SelectedTabId, bool HasUiWindow, bool IsPrivate,
            string KeyboardShortcut = "");

        public static List<PersistedTabGroup> GetTabGroups(long profileId)
        {
            IntPtr ptr = Dll.GetWindows(profileId, out int count);
            var result = new List<PersistedTabGroup>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<WindowNative>();
            for (int i = 0; i < count; i++)
            {
                WindowNative native = Marshal.PtrToStructure<WindowNative>(ptr + (i * structSize));
                result.Add(new PersistedTabGroup(
                    native.id,
                    native.profile_id,
                    Marshal.PtrToStringUTF8(native.name) ?? "",
                    native.is_tab_group != 0,
                    native.selected_tab_id,
                    native.has_ui_window != 0,
                    native.is_private != 0,
                    Marshal.PtrToStringUTF8(native.keyboard_shortcut) ?? ""));
            }
            Dll.FreeWindows(ptr);
            return result;
        }

        public static void MoveTabGroup(long windowId, long nextWindowId) => Dll.MoveWindow(windowId, nextWindowId);

        public static long GetSharedPinnedTabsWindowId(long profileId) => Dll.GetSharedPinnedTabsWindowId(profileId);

        public static long CreateTabGroupEntry(long profileId, bool isTabGroup, bool hasUiWindow)
        {
            return Dll.CreateWindow(profileId,
                (sbyte)(isTabGroup ? 1 : 0),
                (sbyte)(hasUiWindow ? 1 : 0));
        }

        public static void UpdateTabGroupSelectedTab(long tabGroupId, long selectedTabId) => Dll.UpdateWindowSelectedTab(tabGroupId, selectedTabId);

        public static void UpdateTabGroupHasUiWindow(long tabGroupId, bool hasUiWindow)
        {
            Dll.UpdateWindowHasUiWindow(tabGroupId, (sbyte)(hasUiWindow ? 1 : 0));
        }

        public static void UpdateTabGroupIsTabGroup(long tabGroupId, bool isTabGroup)
        {
            Dll.UpdateWindowIsTabGroup(tabGroupId, (sbyte)(isTabGroup ? 1 : 0));
        }

        public static void UpdateTabGroupName(long tabGroupId, string name) => Dll.UpdateWindowName(tabGroupId, name);

        /// <summary>GTK accelerator name (see KeyCombo); "" unbinds.</summary>
        public static void UpdateTabGroupKeyboardShortcut(long tabGroupId, string shortcut) =>
            Dll.UpdateWindowKeyboardShortcut(tabGroupId, shortcut);

        public static void DeleteTabGroup(long tabGroupId) => Dll.CloseWindow(tabGroupId);

        // ---------------------------------------------------------------
        // Tabs
        // ---------------------------------------------------------------

        public record PersistedTab(long Id, long TabGroupId, string Name, string Uri,
            bool IsPinned, long ParentTabId, bool IsCollapsed);

        public static List<PersistedTab> GetTabs(long tabGroupId)
        {
            IntPtr ptr = Dll.GetTabs(tabGroupId, out int count);
            var result = new List<PersistedTab>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<TabNative>();
            for (int i = 0; i < count; i++)
            {
                TabNative native = Marshal.PtrToStructure<TabNative>(ptr + (i * structSize));
                result.Add(new PersistedTab(
                    native.id,
                    native.window_id,
                    Marshal.PtrToStringUTF8(native.name) ?? "",
                    Marshal.PtrToStringUTF8(native.uri) ?? "",
                    native.is_pinned != 0,
                    native.parent_tab_id,
                    native.is_collapsed != 0));
            }
            Dll.FreeTabs(ptr);
            return result;
        }

        public static long CreateTab(long tabGroupId, string uri, string name,
            bool isPinned = false, long parentTabId = 0)
        {
            return Dll.CreateTab(tabGroupId, uri, name,
                (sbyte)(isPinned ? 1 : 0), parentTabId);
        }

        public static void UpdateTabUri(long tabId, string uri) => Dll.UpdateTabUri(tabId, uri);

        public static void UpdateTabName(long tabId, string name) => Dll.UpdateTabName(tabId, name);

        internal static bool SaveTabPinState(long tabId, long windowId, bool updatePin,
            bool isPinned, bool moveToWindow, string uri, string name) =>
            Dll.SaveTabPinState(tabId, windowId, (sbyte)(updatePin ? 1 : 0),
                (sbyte)(isPinned ? 1 : 0), (sbyte)(moveToWindow ? 1 : 0), uri, name) != 0;

        internal static bool ReorderPinnedTab(long tabId, long windowId, long beforeTabId) =>
            Dll.ReorderPinnedTab(tabId, windowId, beforeTabId) != 0;

        public static void UpdateTabGroupId(long tabId, long tabGroupId) => Dll.UpdateTabWindowId(tabId, tabGroupId);

        /// <summary>
        /// Moves a tab using the core's move_tab API.
        /// afterTabId: the tab that should come after the moved tab (0 = append to end).
        /// </summary>
        public static void MoveTab(long tabId, long targetWindowId, long parentTabId = 0,
            long afterTabId = 0, bool isNested = false)
        {
            Dll.MoveTab(tabId, targetWindowId, parentTabId, afterTabId, (sbyte)(isNested ? 1 : 0));
        }

        public static void DeleteTabs(long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeleteTabs(ids, ids.Length);
        }

        // ---------------------------------------------------------------
        // Recently Closed Tabs
        // ---------------------------------------------------------------

        public record RecentlyClosedTab(long Id, long ProfileId, string Name, string Uri, DateTime ClosedAt, long WindowId);

        public static void CreateRecentlyClosedTab(long profileId, string name, string uri)
        {
            Dll.CreateRecentlyClosedTab(profileId, name, uri);
        }

        public static List<RecentlyClosedTab> GetRecentlyClosedTabs(long profileId)
        {
            IntPtr ptr = Dll.GetRecentlyClosedTabs(profileId, out int count);
            var result = new List<RecentlyClosedTab>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<RecentlyClosedTabNative>();
            for (int i = 0; i < count; i++)
            {
                RecentlyClosedTabNative native = Marshal.PtrToStructure<RecentlyClosedTabNative>(
                    ptr + (i * structSize));
                result.Add(new RecentlyClosedTab(
                    native.id,
                    native.profile_id,
                    Marshal.PtrToStringUTF8(native.name) ?? "",
                    Marshal.PtrToStringUTF8(native.uri) ?? "",
                    DateTimeOffset.FromUnixTimeSeconds(native.closed_at).LocalDateTime,
                    native.window_id));
            }
            Dll.FreeRecentlyClosedTabs(ptr);
            return result;
        }

        public static void DeleteRecentlyClosedTabs(long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeleteRecentlyClosedTabs(ids, ids.Length);
        }

        // ---------------------------------------------------------------
        // Recently Closed Windows
        // ---------------------------------------------------------------

        public record RecentlyClosedWindow(long Id, long ProfileId, string Name, DateTime ClosedAt);

        public static List<RecentlyClosedWindow> GetRecentlyClosedWindows(long profileId)
        {
            IntPtr ptr = Dll.GetRecentlyClosedWindows(profileId, out int count);
            var result = new List<RecentlyClosedWindow>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<RecentlyClosedWindowNative>();
            for (int i = 0; i < count; i++)
            {
                RecentlyClosedWindowNative native = Marshal.PtrToStructure<RecentlyClosedWindowNative>(
                    ptr + (i * structSize));
                result.Add(new RecentlyClosedWindow(
                    native.id,
                    native.profile_id,
                    Marshal.PtrToStringUTF8(native.name) ?? "",
                    DateTimeOffset.FromUnixTimeSeconds(native.closed_at).LocalDateTime));
            }
            Dll.FreeRecentlyClosedWindows(ptr);
            return result;
        }

        public static void DeleteRecentlyClosedWindows(long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeleteRecentlyClosedWindows(ids, ids.Length);
        }

        // ---------------------------------------------------------------
        // Undo/Redo Tabs
        // ---------------------------------------------------------------

        public static void CreateUndoRedoTabStep(long windowId, long[] tabIds)
        {
            if (tabIds.Length > 0)
                Dll.CreateUndoRedoTabStep(windowId, tabIds, tabIds.Length);
        }

        public static void ChangeUndoRedoTabStep(long windowId, bool isUndo)
        {
            Dll.ChangeUndoRedoTabStep(windowId, (sbyte)(isUndo ? 1 : 0));
        }

        public static (bool UndoAvailable, bool RedoAvailable) IsUndoRedoTabStepAvailable(long windowId)
        {
            Dll.IsUndoRedoTabStepAvailable(windowId, out sbyte undo, out sbyte redo);
            return (undo != 0, redo != 0);
        }

        // ---------------------------------------------------------------
        // Favicons
        // ---------------------------------------------------------------

        public record FaviconResult(byte[] Data, string? IconUrl, bool Expired);

        public static void SetFavicon(string pageUrl, string iconUrl, byte[] pngData) => Dll.SetFavicon(pageUrl, iconUrl, pngData, pngData.Length);

        public static FaviconResult? GetFavicon(string pageUrl)
        {
            IntPtr ptr = Dll.GetFavicon(pageUrl, out int size, out sbyte expired, out string? iconUrl);
            if (ptr == IntPtr.Zero)
                return null;

            byte[] data = new byte[size];
            Marshal.Copy(ptr, data, 0, size);
            Dll.FreeFavicon(ptr);
            return new FaviconResult(data, iconUrl, expired != 0);
        }

        // ---------------------------------------------------------------
        // Passwords
        // ---------------------------------------------------------------

        public static long[] GetPasswordItemIds(long profileId, string searchText = "", string authority = "")
        {
            IntPtr ptr = Dll.GetPasswordItemIds(profileId, searchText, authority, out int count);
            if (ptr == IntPtr.Zero)
                return [];

            long[] ids = new long[count];
            Marshal.Copy(ptr, ids, 0, count);
            Dll.FreePasswordItemIds(ptr);
            return ids;
        }

        public static List<PasswordEntry> GetPasswordEntries(long[] ids)
        {
            IntPtr ptr = Dll.GetPasswordItemsByIds(ids, ids.Length, out int count);
            var result = new List<PasswordEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<PasswordItemNative>();
            for (int i = 0; i < count; i++)
            {
                PasswordItemNative native = Marshal.PtrToStructure<PasswordItemNative>(ptr + (i * structSize));
                result.Add(new PasswordEntry
                {
                    Id = native.Id,
                    ProfileId = native.ProfileId,
                    Url = Marshal.PtrToStringUTF8(native.Url) ?? "",
                    Username = Marshal.PtrToStringUTF8(native.Username) ?? "",
                    Password = Marshal.PtrToStringUTF8(native.Password) ?? "",
                    VerificationKey = Marshal.PtrToStringUTF8(native.VerificationKey) ?? "",
                    VerificationCode = Marshal.PtrToStringUTF8(native.VerificationCode) ?? "",
                });
            }
            Dll.FreePasswordItems(ptr, count);
            return result;
        }

        public static string ExportPasswordItemsCsv(long[] ids)
        {
            return Dll.ExportPasswordItemsCsv(ids, ids.Length) ?? "";
        }

        public static (bool success, int imported, int notImported) ImportPasswordItemsFromCsv(
            long profileId, string csvText)
        {
            bool ok = Dll.ImportPasswordItemsFromCsv(profileId, csvText,
                out int imported, out int notImported);
            return (ok, imported, notImported);
        }

        public static long CreatePasswordEntry(long profileId, PasswordEntry entry, bool updateExisting = true)
        {
            return Dll.CreatePasswordItem(profileId, entry.Url, entry.Username, entry.Password,
                entry.VerificationKey, entry.VerificationCode, updateExisting);
        }

        public static void UpdatePasswordEntry(PasswordEntry entry)
        {
            Dll.UpdatePasswordItem(entry.Id, entry.Url, entry.Username, entry.Password,
                entry.VerificationKey, entry.VerificationCode);
        }

        public static void DeletePasswordEntries(params long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeletePasswordItems(ids, ids.Length);
        }

        public static long CreateNeverSavePasswordItem(long profileId, string url)
        {
            return Dll.CreateNeverSavePasswordItem(profileId, url);
        }

        public static bool GetNeverSavePasswordItem(long profileId, string authority)
        {
            return Dll.GetNeverSavePasswordItem(profileId, authority);
        }

        public static void CreateSnapshot(long profileId) => Dll.CreateSnapshot(profileId);

        public static long GetLastSnapshotTimestamp(long profileId) => Dll.GetLastSnapshotTimestamp(profileId);

        public static long RestoreSnapshot(long profileId, long createdAt) => Dll.RestoreSnapshot(profileId, createdAt);

        // ───── Sync (file export / import) ───────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SyncProgressCallback(int done, int total,
            [MarshalAs(UnmanagedType.LPWStr)] string description);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SyncCompletionCallback(int success,
            [MarshalAs(UnmanagedType.LPWStr)] string? errorMessage);

        private static SyncProgressCallback? s_syncProgressCb;
        private static SyncCompletionCallback? s_syncCompletionCb;

        public static void ExportProfileData(long profileId, string directory,
            SyncProgressCallback? progressCb, SyncCompletionCallback completionCb)
        {
            s_syncProgressCb = progressCb;
            s_syncCompletionCb = completionCb;
            Dll.ExportProfileData(profileId, directory, s_syncProgressCb, s_syncCompletionCb);
        }

        public static void ImportProfileData(long profileId, string source,
            SyncProgressCallback? progressCb, SyncCompletionCallback completionCb)
        {
            s_syncProgressCb = progressCb;
            s_syncCompletionCb = completionCb;
            Dll.ImportProfileData(profileId, source, s_syncProgressCb, s_syncCompletionCb);
        }

        public static void CancelSync() => Dll.CancelSync();

        // ───── Notifications ─────────────────────────────────────────────────

#pragma warning disable IDE0052
        private static ShowNotificationCallback? s_showNotificationCb;
        private static ClearNotificationCallback? s_clearNotificationCb;
#pragma warning restore IDE0052

        /// <summary>
        /// Installs the desktop-notification presenter the core calls for
        /// browser.notifications (see Services/NotificationService.cs). The
        /// delegates stay rooted here for the process lifetime.
        /// </summary>
        public static void SetupNotificationCallbacks(Action<string> show, Action<int, string, string> clear)
        {
            s_showNotificationCb = json => show(json);
            s_clearNotificationCb = (source, sourceId, id) => clear(source, sourceId, id);
            Dll.SetNotificationCallbacks(s_showNotificationCb, s_clearNotificationCb);
        }

        /// <summary>Reports interaction with a shown notification to the core. UI thread only.</summary>
        public static void NotifyNotificationEvent(
            NotificationEventType type, NotificationSource source,
            string sourceId, string notificationId, int buttonIndex, bool byUser)
            => Dll.NotifyNotificationEvent((int)type, (int)source, sourceId, notificationId, buttonIndex, byUser ? 1 : 0);

        // ───── Downloads ─────────────────────────────────────────────────────

#pragma warning disable IDE0052
        private static DownloadChangedCallback? s_downloadCreatedCb;
        private static DownloadChangedCallback? s_downloadUpdatedCb;
        private static DownloadDeletedCallback? s_downloadDeletedCb;
#pragma warning restore IDE0052

        public static event Action<DownloadEntry>? DownloadCreated;
        public static event Action<DownloadEntry>? DownloadUpdated;
        public static event Action<long>? DownloadDeleted;

        private static DownloadEntry NativeToDownloadEntry(IntPtr ptr)
        {
            DownloadEntryNative native = Marshal.PtrToStructure<DownloadEntryNative>(ptr);
            return new DownloadEntry
            {
                Id = native.id,
                ProfileId = native.profile_id,
                Url = Marshal.PtrToStringUTF8(native.origin_uri) ?? "",
                FileName = Marshal.PtrToStringUTF8(native.name) ?? "",
                FilePath = Marshal.PtrToStringUTF8(native.path) ?? "",
                Status = (DownloadStatus)native.operation_state,
                TotalBytes = native.total_size,
                ReceivedBytes = native.current_size,
                StartTime = DateTimeOffset.FromUnixTimeSeconds(native.timestamp).LocalDateTime,
            };
        }

        public static void SetupDownloadCallbacks()
        {
            s_downloadCreatedCb = ptr => DownloadCreated?.Invoke(NativeToDownloadEntry(ptr));
            s_downloadUpdatedCb = ptr => DownloadUpdated?.Invoke(NativeToDownloadEntry(ptr));
            s_downloadDeletedCb = id => DownloadDeleted?.Invoke(id);
            Dll.SetDownloadCallbacks(s_downloadCreatedCb, s_downloadUpdatedCb, s_downloadDeletedCb);
        }

        public static List<DownloadEntry> GetDownloads(long profileId)
        {
            IntPtr ptr = Dll.GetDownloads(profileId, out int count);
            var result = new List<DownloadEntry>(count);
            if (ptr == IntPtr.Zero)
                return result;

            int structSize = Marshal.SizeOf<DownloadEntryNative>();
            for (int i = 0; i < count; i++)
            {
                DownloadEntryNative native = Marshal.PtrToStructure<DownloadEntryNative>(ptr + (i * structSize));
                result.Add(new DownloadEntry
                {
                    Id = native.id,
                    Url = Marshal.PtrToStringUTF8(native.origin_uri) ?? "",
                    FileName = Marshal.PtrToStringUTF8(native.name) ?? "",
                    FilePath = Marshal.PtrToStringUTF8(native.path) ?? "",
                    Status = (DownloadStatus)native.operation_state,
                    TotalBytes = native.total_size,
                    ReceivedBytes = native.current_size,
                    StartTime = DateTimeOffset.FromUnixTimeSeconds(native.timestamp).LocalDateTime,
                });
            }

            Dll.FreeDownloads(ptr);
            return result;
        }

        public static void DeleteDownloads(long[] ids)
        {
            if (ids.Length > 0)
                Dll.DeleteDownloads(ids, ids.Length);
        }

        public static void CancelDownload(long downloadId)
        {
            Dll.CancelDownload(downloadId);
        }

        public static void RestartDownload(long downloadId)
        {
            Dll.RestartDownload(downloadId);
        }

    }

}
