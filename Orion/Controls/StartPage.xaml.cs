using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orion
{
    public sealed partial class StartPage : UserControl
    {
        public StartPage()
        {
            InitializeComponent();
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url })
                NavigateRequested?.Invoke(this, url);
        }

        private void EditAddress_Click(object sender, RoutedEventArgs e) =>
            AddressRequested?.Invoke(this, System.EventArgs.Empty);

        public event System.EventHandler? AddressRequested;

        public event System.EventHandler<string>? NavigateRequested;
    }
}
