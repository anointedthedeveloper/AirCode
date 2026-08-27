using System.Windows;
using AirCode.Services;

namespace AirCode.Views;

public partial class HostSetupDialog : Window
{
    public string DetectedIp { get; private set; } = "";

    public HostSetupDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DetectNetwork();
    }

    private void DetectNetwork()
    {
        var adapters = HotspotService.GetAllAdapters();
        var best     = HotspotService.GetBestLocalIp();

        DetectedIp = best;

        if (string.IsNullOrEmpty(best))
        {
            IpText.Text          = "Not connected";
            AdapterText.Text     = "Connect to a Wi-Fi network and reopen this dialog.";
            NoNetworkText.Visibility = Visibility.Visible;
        }
        else
        {
            IpText.Text = best;
            var adapter = adapters.FirstOrDefault();
            AdapterText.Text = adapter.name ?? "Network adapter detected";
            NoNetworkText.Visibility = Visibility.Collapsed;
        }
    }

    private void Start_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(DetectedIp))
        {
            MessageBox.Show(
                "No network detected.\n\nConnect this PC to a Wi-Fi network (phone hotspot, Windows hotspot, or router) then try again.",
                "No Network", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
