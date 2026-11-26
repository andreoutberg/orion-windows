using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using Windows.UI.ViewManagement.Core;

namespace Orion
{
    public sealed partial class SettingsPage
    {
        private static readonly string[] _profileColors =
        [
            "#2646DE", "#871AD6", "#FF75CC", "#FF4040",
            "#FF9032", "#FFE340", "#59C214", "#858585",
        ];

        private void InitializeProfilesData()
        {
            KeyboardShortcutRecorder.ChordCaptured += KeyboardShortcutRecorder_ChordCaptured;
            KeyboardShortcutRecorder.ClearRequested += KeyboardShortcutRecorder_ClearRequested;

            foreach (string color in _profileColors)
            {
                ProfileColorGridView.Items.Add(new Ellipse
                {
                    Width = 20,
                    Height = 20,
                    Fill = new SolidColorBrush(ParseHexColor(color)),
                    Tag = Profile.ColorToIcon(color),
                });
            }

            RefreshProfiles();
        }

        private static Windows.UI.Color ParseHexColor(string hex)
        {
            hex = hex.TrimStart('#');
            return Windows.UI.Color.FromArgb(255,
                byte.Parse(hex[0..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
        }

        /// <summary>
        /// First palette color no existing profile uses as its icon, or the
        /// first palette color when every one is taken. Emoji icons don't
        /// count as palette usage.
        /// </summary>
        internal static string PickUnusedProfileIcon()
        {
            var usedIcons = OrionBackend.GetProfiles().Select(p => p.Icon).ToHashSet();
            string? freeColor = _profileColors.FirstOrDefault(c => !usedIcons.Contains(Profile.ColorToIcon(c)));
            return Profile.ColorToIcon(freeColor ?? _profileColors[0]);
        }

        /// <summary>
        /// Creates a profile named "New Profile" with an unused palette icon,
        /// inheriting the browser engine of <paramref name="sourceProfileId"/>
        /// (the build default when 0/unknown). Returns the new profile id, or 0 on
        /// failure. The profile's own database is provisioned on its first
        /// launch.
        /// </summary>
        internal static long CreateProfileLike(long sourceProfileId)
        {
            string engine = App.DefaultBrowserEngine;
            if (App.SupportsWebKit)
            {
                engine = OrionBackend.GetProfiles()
                    .FirstOrDefault(p => p.Id == sourceProfileId)?.Engine ?? engine;
            }
            return OrionBackend.CreateProfile("New Profile", PickUnusedProfileIcon(), engine);
        }

        /// <summary>
        /// Selects the given profile in the Profiles category and focuses the
        /// name field with its text selected (used by "New Profile" so the
        /// detail panel shows the newly created profile, ready for typing a
        /// name over the placeholder).
        /// </summary>
        internal void SelectProfile(long profileId)
        {
            RefreshProfiles();
            Profile? profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null)
                return;
            ProfileListView.SelectedItem = profile;

            // Deferred: the detail panel becomes visible during the selection
            // change above; focusing works reliably only after that layout pass.
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _ = ProfileNameTextBox.Focus(FocusState.Programmatic);
                ProfileNameTextBox.SelectAll();
            });
        }

        private void RefreshProfiles()
        {
            long selectedId = _selectedProfile?.Id ?? 0;
            _profiles.Clear();
            foreach (Profile p in OrionBackend.GetProfiles())
                _profiles.Add(p);

            ProfileListView.ItemsSource = _profiles;

            Profile? toSelect = selectedId != 0
                ? _profiles.FirstOrDefault(p => p.Id == selectedId)
                : _profiles.FirstOrDefault();
            if (toSelect != null)
                ProfileListView.SelectedItem = toSelect;
        }

        private void SaveSelectedProfile()
        {
            if (_selectedProfile != null)
            {
                OrionBackend.UpdateProfile(_selectedProfile);
                if (_selectedProfile.IsDefault)
                    MainViewModel?.RefreshActiveProfile();
            }
        }

        private void SelectColorInGrid(string icon)
        {
            int colorIndex = -1;
            for (int i = 0; i < ProfileColorGridView.Items.Count; i++)
            {
                if (ProfileColorGridView.Items[i] is Ellipse e && (string)e.Tag == icon)
                {
                    colorIndex = i;
                    break;
                }
            }
            ProfileColorGridView.SelectedIndex = colorIndex;

            // Show emoji on the button if icon is an emoji
            if (colorIndex < 0 && !string.IsNullOrEmpty(icon) && !icon.StartsWith('<'))
                EmojiButtonLabel.Text = icon;
            else
                EmojiButtonLabel.Text = "🚀";
        }

        private void ProfileListView_SelectionChanged(object _sender, SelectionChangedEventArgs _e)
        {
            if (ProfileListView.SelectedItem is not Profile profile)
            {
                ProfileDetailPanel.Visibility = Visibility.Collapsed;
                _selectedProfile = null;
                return;
            }

            _selectedProfile = profile;
            _isUpdatingUI = true;

            ProfileNameTextBox.Text = profile.Name;
            ShowInAddressBarToggle.IsOn = profile.ShowInAddressBar;
            ShowBadgeInAppIconToggle.IsOn = profile.ShowBadgeInAppIcon;
            // Legacy free-text values fail the parse and display as unbound;
            // the stored string is kept until the user records a new chord.
            KeyboardShortcutRecorder.ShowCombo(
                KeyCombo.TryParseGtkAccel(profile.KeyboardShortcut, out KeyCombo shortcut) ? shortcut : null);
            KeyboardShortcutError.Visibility = Visibility.Collapsed;
            SetDefaultButton.IsEnabled = !profile.IsDefault;

            BrowserEngineComboBox.SelectedIndex = profile.Engine == "blink" ? 1 : 0;
            BrowserEngineComboBox.IsEnabled = App.SupportsWebKit || profile.Engine == "webkit";

            SelectColorInGrid(profile.Icon);

            _isUpdatingUI = false;
            ProfileDetailPanel.Visibility = Visibility.Visible;
        }

        private void ProfileName_TextChanged(object _sender, TextChangedEventArgs _e)
        {
            if (_isUpdatingUI || _selectedProfile == null)
                return;
            _selectedProfile.Name = ProfileNameTextBox.Text;
            SaveSelectedProfile();
        }

        private void ProfileColor_SelectionChanged(object _sender, SelectionChangedEventArgs _e)
        {
            if (_isUpdatingUI || _selectedProfile == null)
                return;
            if (ProfileColorGridView.SelectedItem is Ellipse ellipse)
            {
                _selectedProfile.Icon = (string)ellipse.Tag;
                EmojiButtonLabel.Text = "🚀";
                SaveSelectedProfile();
            }
        }

        private void OpenEmojiPicker_Click(object _sender, RoutedEventArgs _e)
        {
            _openEmojiPicker = true;
            _ = EmojiCaptureTextBox.Focus(FocusState.Programmatic);
        }

        private void EmojiCaptureTextBox_GotFocus(object _sender, RoutedEventArgs _e)
        {
            if (!_openEmojiPicker)
                return;
            _openEmojiPicker = false;
            _ = CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Emoji);
        }

        private void EmojiCapture_TextChanged(object _sender, TextChangedEventArgs _e)
        {
            if (_sender is not TextBox textBox || string.IsNullOrEmpty(textBox.Text))
                return;

            if (_selectedProfile != null)
            {
                _selectedProfile.Icon = textBox.Text;
                EmojiButtonLabel.Text = textBox.Text;
                ProfileColorGridView.SelectedIndex = -1;
                SaveSelectedProfile();
            }
            textBox.Text = "";
        }

        private void ShowInAddressBar_Toggled(object _sender, RoutedEventArgs _e)
        {
            if (_isUpdatingUI || _selectedProfile == null)
                return;
            _selectedProfile.ShowInAddressBar = ShowInAddressBarToggle.IsOn;
            SaveSelectedProfile();
        }

        private void ShowBadgeInAppIcon_Toggled(object _sender, RoutedEventArgs _e)
        {
            if (_isUpdatingUI || _selectedProfile == null)
                return;
            _selectedProfile.ShowBadgeInAppIcon = ShowBadgeInAppIconToggle.IsOn;
            SaveSelectedProfile();
        }

        private void KeyboardShortcutRecorder_ChordCaptured(ShortcutRecorderBox recorder, KeyCombo combo)
        {
            if (_selectedProfile == null)
                return;
            // Conflicts block, never steal: built-in shortcuts always win at
            // dispatch, and a profile duplicate would silently shadow the
            // later profile.
            string? conflict = EntityShortcuts.BuiltinUsing(combo) is string builtin
                ? $"{combo.ToDisplayString()} is used by “{builtin}”"
                : EntityShortcuts.ProfileUsing(combo, excludeProfileId: _selectedProfile.Id) is string profile
                    ? $"{combo.ToDisplayString()} is used by profile “{profile}”"
                    : null;
            if (conflict != null)
            {
                KeyboardShortcutError.Text = conflict;
                KeyboardShortcutError.Visibility = Visibility.Visible;
                recorder.ShowCombo(
                    KeyCombo.TryParseGtkAccel(_selectedProfile.KeyboardShortcut, out KeyCombo current) ? current : null);
                return;
            }
            KeyboardShortcutError.Visibility = Visibility.Collapsed;
            _selectedProfile.KeyboardShortcut = combo.ToGtkAccelString();
            SaveSelectedProfile();
            EntityShortcuts.Refresh();
            recorder.ShowCombo(combo);
        }

        private void KeyboardShortcutRecorder_ClearRequested(ShortcutRecorderBox recorder)
        {
            if (_selectedProfile == null)
                return;
            KeyboardShortcutError.Visibility = Visibility.Collapsed;
            _selectedProfile.KeyboardShortcut = string.Empty;
            SaveSelectedProfile();
            EntityShortcuts.Refresh();
            recorder.ShowCombo(null);
        }

        private void SetDefault_Click(object _sender, RoutedEventArgs _e)
        {
            if (_selectedProfile == null || _selectedProfile.IsDefault)
                return;

            OrionBackend.SetDefaultProfile(_selectedProfile.Id);
            RefreshProfiles();
            MainViewModel?.RefreshActiveProfile();
        }

        private void AddProfile_Click(object _sender, RoutedEventArgs _e)
        {
            long newId = CreateProfileLike(MainViewModel?.ActiveProfileId ?? 0);
            if (newId == 0)
                return;

            SelectProfile(newId);
        }

        private void ProfileListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (args.Items.Count == 0 || args.Items[0] is not Profile moved)
                return;

            int index = _profiles.IndexOf(moved);
            if (index < 0)
                return;

            long nextId = index + 1 < _profiles.Count ? _profiles[index + 1].Id : 0;
            OrionBackend.MoveProfile(moved.Id, nextId);
        }

        private async void BrowserEngine_SelectionChanged(object _sender, SelectionChangedEventArgs _e)
        {
            if (_isUpdatingUI || _selectedProfile == null)
                return;
            if (BrowserEngineComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string engine)
                return;

            // Capture: the selection could move to another profile while the
            // confirmation dialog is open.
            Profile profile = _selectedProfile;
            string previous = profile.Engine;
            if (engine == previous)
                return;

            string engineName = engine == "blink" ? "Blink (Chromium)" : "WebKit";
            string message =
                "Data stored by Orion — history, bookmarks, tabs, passwords, and settings — "
                + "is shared between engines and carries over.\n\n"
                + "Website data (cookies, logins, caches) is engine-specific and does not "
                + "carry over, so websites will ask you to sign in again. Extensions are "
                + "currently only supported on WebKit.";
            if (profile.Id == OrionBackend.GetCurrentProfileId())
            {
                message += "\n\nThis profile's window needs to be reopened for the engine "
                    + "change to take effect.";
            }

            bool confirmed = await PopupDialog.ShowConfirmAsync(this,
                $"Switch \"{profile.Name}\" to {engineName}?",
                message,
                confirmButton: "Switch Engine");

            if (!confirmed)
            {
                _isUpdatingUI = true;
                BrowserEngineComboBox.SelectedIndex = previous == "blink" ? 1 : 0;
                _isUpdatingUI = false;
                return;
            }

            profile.Engine = engine;
            OrionBackend.UpdateProfile(profile);
            BrowserEngineComboBox.IsEnabled = App.SupportsWebKit;
        }

        private async void DeleteProfile_Click(object _sender, RoutedEventArgs _e)
        {
            if (_selectedProfile == null || _selectedProfile.IsInternal)
                return;

            Profile profile = _selectedProfile;
            bool isOwn = profile.Id == OrionBackend.GetCurrentProfileId();
            string message = "This permanently deletes the profile and its browsing data — history, "
                + "bookmarks, tabs, passwords, settings, and extensions. This cannot be undone.";
            if (isOwn)
                message += "\n\nThis profile's windows will close, and Orion will switch to the default profile.";
            else if (SingleInstance.IsRunning(profile.Id))
                message += "\n\nThe profile's open windows will be closed.";
            bool confirmed = await PopupDialog.ShowConfirmAsync(this,
                $"Delete \"{profile.Name}\"?", message,
                confirmButton: "Delete Profile");
            if (!confirmed)
                return;

            bool wasDefault = profile.IsDefault;
            // A running instance closes its windows on the pipe message; its
            // data dirs are removed once it is fully gone (ProfileDataCleanup).
            if (!isOwn)
                _ = SingleInstance.NotifyProfileDeleted(profile.Id);
            if (!OrionBackend.DeleteProfile(profile.Id))
            {
                await PopupDialog.ShowMessageAsync(this, "Delete Profile",
                    "The last remaining profile cannot be deleted.");
                return;
            }

            if (isOwn)
            {
                // Mirrors macOS/GTK: restart into the default profile, whose
                // launch-time cleanup removes our data dirs once we are gone.
                Profile? next = OrionBackend.GetProfiles().FirstOrDefault(p => p.IsDefault);
                OrionBackend.LogInfo($"DeleteProfile: own profile {profile.Id} deleted; "
                    + $"switching to profile {next?.Id.ToString() ?? "none"} and exiting");
                if (next != null)
                    _ = MainWindow.OpenForProfile(next);
                MainWindow.ExitAll();
                return;
            }

            ProfileDataCleanup.RunFor(profile.Id);
            // Select the profile that takes the deleted one's place (the
            // previous one when deleting the last) so the detail panel
            // stays populated.
            int index = _profiles.IndexOf(profile);
            RefreshProfiles();
            ProfileListView.SelectedItem = _profiles[Math.Min(index, _profiles.Count - 1)];
            // Drop the deleted profile's shortcut from the dispatch cache.
            EntityShortcuts.Refresh();
            if (wasDefault)
                MainViewModel?.RefreshActiveProfile();
        }
    }
}
