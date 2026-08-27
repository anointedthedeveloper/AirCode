using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AirCode.Helpers;
using AirCode.Services;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class SettingsPage : UserControl
{
    private MainViewModel? _vm;
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        _loading = true;
        NameBox.Text = vm.Settings.DisplayName;
        FolderBox.Text = vm.Settings.DownloadFolder;
        NotifCheck.IsChecked = vm.Settings.NotificationsEnabled;
        LightBtn.IsChecked = !vm.Settings.DarkMode;
        DarkBtn.IsChecked = vm.Settings.DarkMode;
        _loading = false;

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ConnectionState) or nameof(MainViewModel.IsConnected))
                Dispatcher.InvokeAsync(UpdateNetworkInfo);
        };
        UpdateNetworkInfo();
    }

    private void UpdateNetworkInfo()
    {
        if (_vm == null) return;
        NetStatus.Text = _vm.ConnectionStatusText;
        NetIp.Text = _vm.HostIp ?? "—";
        NetRole.Text = _vm.IsHost ? "Host" : (_vm.IsConnected ? "Client" : "Not connected");
    }

    private async void UpdateName_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("Name cannot be empty."); return; }
        await _vm.UpdateDisplayNameAsync(name);
        MessageBox.Show("Display name updated.", "AirCode", MessageBoxButton.OK);
    }

    private void Theme_Changed(object s, RoutedEventArgs e)
    {
        if (_loading || _vm == null) return;
        bool dark = DarkBtn.IsChecked == true;
        _vm.Settings.DarkMode = dark;
        _vm.IsDarkMode = dark;
        _vm.SaveSettings();

        // Swap theme resource dictionaries
        var app = Application.Current;
        var merged = app.Resources.MergedDictionaries;
        var themeDict = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Theme") == true);
        if (themeDict != null) merged.Remove(themeDict);

        var newTheme = dark ? "Views/Themes/DarkTheme.xaml" : "Views/Themes/LightTheme.xaml";
        merged.Insert(0, new System.Windows.ResourceDictionary
        {
            Source = new Uri(newTheme, UriKind.Relative)
        });
    }

    private void BrowseFolder_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var win = Window.GetWindow(this);
        var selected = FolderPicker.PickFolder(_vm.Settings.DownloadFolder, win);
        if (selected != null)
        {
            _vm.Settings.DownloadFolder = selected;
            _vm.Db.SaveSettings(_vm.Settings);
            FolderBox.Text = selected;
        }
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var folder = _vm.Settings.DownloadFolder;
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", folder);
    }

    private void Notif_Changed(object s, RoutedEventArgs e)
    {
        if (_loading || _vm == null) return;
        _vm.Settings.NotificationsEnabled = NotifCheck.IsChecked == true;
        NotificationService.Instance.IsEnabled = _vm.Settings.NotificationsEnabled;
        _vm.Db.SaveSettings(_vm.Settings);
    }
}
