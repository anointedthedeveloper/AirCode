using System.Windows;

namespace AirCode.Views;

public partial class ManualConnectDialog : Window
{
    public string IpAddress { get; private set; } = "";

    public ManualConnectDialog() => InitializeComponent();

    private void Connect_Click(object s, RoutedEventArgs e)
    {
        IpAddress = IpBox.Text.Trim();
        if (string.IsNullOrEmpty(IpAddress)) { MessageBox.Show("Please enter an IP address."); return; }
        DialogResult = true;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
