using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirCode.Services;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class HomePage : UserControl
{
    private MainViewModel? _vm;
    private MainWindow?    _win;

    public HomePage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm  = vm;
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
            StartHostBtn.Visibility  = connected ? Visibility.Collapsed : Visibility.Visible;
            ConnectBtn.Visibility    = connected ? Visibility.Collapsed : Visibility.Visible;
            DisconnectBtn.Visibility = connected ? Visibility.Visible   : Visibility.Collapsed;
            if (connected) ManualPanel.Visibility = Visibility.Collapsed;
            HostIpText.Text = _vm?.HostIp != null && _vm.IsConnected
                ? $"Host IP: {_vm.HostIp}"
                : "";
        });
    }

    // ── Start Network ─────────────────────────────────────────────────────────

    private async void StartHost_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;

        var dlg = new HostSetupDialog { Owner = _win };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true);
        var (ok, msg) = await _vm.StartHostAsync();
        SetBusy(false);

        if (!ok)
            ShowInlineError(msg);
        else
            NotificationService.Instance.Show("Hosting",
                $"Running on {_vm.HostIp}", NotificationKind.Success);
    }

    // ── Auto Connect ──────────────────────────────────────────────────────────

    private async void Connect_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        ManualPanel.Visibility = Visibility.Collapsed;

        SetBusy(true);
        await _vm.ConnectAsync();
        SetBusy(false);

        if (!_vm.IsConnected)
        {
            // Show inline manual IP panel instead of an off-screen MessageBox
            ManualIpBox.Text = GuessHostIp();
            ManualPanel.Visibility = Visibility.Visible;
            ManualIpBox.Focus();
            ManualIpBox.SelectAll();
        }
    }

    // ── Manual IP connect ─────────────────────────────────────────────────────

    private async void ManualConnect_Click(object s, RoutedEventArgs e)
        => await DoManualConnect();

    private async void ManualIp_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await DoManualConnect();
    }

    private async Task DoManualConnect()
    {
        if (_vm == null) return;
        var ip = ManualIpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip)) return;

        SetBusy(true);
        await _vm.ConnectAsync(ip);
        SetBusy(false);

        if (_vm.IsConnected)
            ManualPanel.Visibility = Visibility.Collapsed;
        else
            ManualIpBox.BorderBrush = System.Windows.Media.Brushes.Red;
    }

    private void CloseManual_Click(object s, RoutedEventArgs e)
        => ManualPanel.Visibility = Visibility.Collapsed;

    // ── Disconnect ────────────────────────────────────────────────────────────

    private async void Disconnect_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.DisconnectAsync();
    }

    // ── Quick actions ─────────────────────────────────────────────────────────

    private void SendFile_Click(object s, RoutedEventArgs e)  => _win?.Nav_Files(s, e);
    private void ShareCode_Click(object s, RoutedEventArgs e) => _win?.Nav_Code(s, e);
    private void OpenChat_Click(object s, RoutedEventArgs e)  => _win?.Nav_Chat(s, e);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        StartHostBtn.IsEnabled = !busy;
        ConnectBtn.IsEnabled   = !busy;
    }

    private void ShowInlineError(string msg)
    {
        NotificationService.Instance.Show("Error", msg.Split('\n')[0], NotificationKind.Error);
    }

    /// <summary>Guess the most likely host IP based on the client's own subnet.</summary>
    private static string GuessHostIp()
    {
        var adapters = HotspotService.GetAllAdapters();
        if (adapters.Count == 0) return "192.168.1.1";

        // Take the first adapter IP and replace last octet with .1
        var ip = adapters[0].ip;
        var parts = ip.Split('.');
        if (parts.Length == 4)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.1";

        return ip;
    }
}
