using System.Windows;

namespace AirCode.Views;

public partial class HostSetupDialog : Window
{
    public string NetworkName { get; private set; } = "AirCode-Classroom";
    public string Password { get; private set; } = "aircode2024";

    public HostSetupDialog()
    {
        InitializeComponent();
        NameBox.Text = "AirCode-Classroom";
        PassBox.Password = "aircode2024";
    }

    private void Start_Click(object s, RoutedEventArgs e)
    {
        NetworkName = NameBox.Text.Trim();
        Password = PassBox.Password;
        if (string.IsNullOrEmpty(NetworkName)) { MessageBox.Show("Please enter a network name."); return; }
        if (Password.Length < 8) { MessageBox.Show("Password must be at least 8 characters."); return; }
        DialogResult = true;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
