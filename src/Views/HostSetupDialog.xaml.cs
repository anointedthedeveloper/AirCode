using System.Windows;
using AirCode.Models;

namespace AirCode.Views;

public partial class HostSetupDialog : Window
{
    public bool   UseHotspot { get; private set; } = true;
    public string Ssid       { get; private set; } = "";
    public string Password   { get; private set; } = "";

    public HostSetupDialog(AppSettings settings)
    {
        InitializeComponent();
        SsidBox.Text = settings.LastNetworkName;
        PasswordBox.Text = settings.LastNetworkPassword;
        UseHotspotBox.IsChecked = settings.UseHotspot;
        Hotspot_Toggled(null, null!);
    }

    private void Hotspot_Toggled(object? s, RoutedEventArgs e)
    {
        // Fires during InitializeComponent before the fields exist
        if (HotspotFields == null || NoHotspotText == null) return;
        var on = UseHotspotBox.IsChecked == true;
        HotspotFields.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        NoHotspotText.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Start_Click(object s, RoutedEventArgs e)
    {
        UseHotspot = UseHotspotBox.IsChecked == true;
        Ssid = SsidBox.Text.Trim();
        Password = PasswordBox.Text;

        if (UseHotspot)
        {
            string? err = null;
            if (Ssid.Length == 0) err = "Enter a network name.";
            else if (Password.Length < 8 || Password.Length > 63) err = "The password must be 8–63 characters.";
            if (err != null)
            {
                ErrorText.Text = err;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
