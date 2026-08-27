using System.Windows;
using System.Windows.Controls;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class HomePage : UserControl
{
    private MainViewModel? _vm;
    private MainWindow? _win;

    public HomePage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        _win = Window.GetWindow(this) as MainWindow;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ConnectionState)
                                or nameof(MainViewModel.IsConnected))
                UpdateButtons();
        };
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        Dispatcher.InvokeAsync(() =>
        {
            bool connected = _vm?.IsConnected ?? false;
            StartHostBtn.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            ConnectBtn.Visibility   = connected ? Visibility.Collapsed : Visibility.Visible;
            DisconnectBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            HostIpText.Text = _vm?.HostIp != null ? $"Host IP: {_vm.HostIp}" : "";
        });
    }

    private async void StartHost_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var dlg = new HostSetupDialog { Owner = _win };
        if (dlg.ShowDialog() == true)
        {
            StartHostBtn.IsEnabled = false;
            var (ok, msg) = await _vm.StartHostAsync(dlg.NetworkName, dlg.Password);
            StartHostBtn.IsEnabled = true;
            if (!ok)
                MessageBox.Show(msg, "AirCode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Connect_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        ConnectBtn.IsEnabled = false;
        await _vm.ConnectAsync();
        ConnectBtn.IsEnabled = true;

        if (!_vm.IsConnected)
        {
            var r = MessageBox.Show(
                "Could not find an AirCode host automatically.\n\nEnter the host IP address manually?",
                "AirCode", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                var manualDlg = new ManualConnectDialog { Owner = _win };
                if (manualDlg.ShowDialog() == true)
                {
                    ConnectBtn.IsEnabled = false;
                    await _vm.ConnectAsync(manualDlg.IpAddress);
                    ConnectBtn.IsEnabled = true;
                }
            }
        }
    }

    private async void Disconnect_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.DisconnectAsync();
    }

    private void SendFile_Click(object s, RoutedEventArgs e) =>
        (_win as MainWindow)?.GetType().GetMethod("Nav_Files")?.Invoke(_win, new object[] { s, e });

    private void ShareCode_Click(object s, RoutedEventArgs e) =>
        _win?.GetType().GetField("CodePage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private void OpenChat_Click(object s, RoutedEventArgs e)
    {
        if (_win == null) return;
        _win.GetType().GetMethod("Nav_Chat",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(_win, new object[] { s, e });
    }
}
