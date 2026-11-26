using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Orion
{
    public sealed partial class MainWindowContent
    {
        public Visibility PrivateWindowIndicatorVisibility =>
            PrivateBrowsing.IsActive ? Visibility.Visible : Visibility.Collapsed;

        private MenuFlyoutItem CreatePrivateWindowMenuItem()
        {
            var item = new MenuFlyoutItem
            {
                Text = "New Private Window",
                IsEnabled = OrionBackend.IsBlinkEngine,
                KeyboardAcceleratorTextOverride = ShortcutText("NewPrivateWindow"),
            };
            item.Click += (_, _) => OpenPrivateWindow();
            return item;
        }

        private async void OpenPrivateWindow()
        {
            if (!OrionBackend.IsBlinkEngine)
                return;

            try
            {
                PrivateBrowsing.OpenWindow();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                or System.IO.IOException or UnauthorizedAccessException)
            {
                _ = await PopupDialog.ShowMessageAsync(this, "Private window could not open", ex.Message);
            }
        }
    }
}
