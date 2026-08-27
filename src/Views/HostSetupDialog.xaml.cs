using System.Windows;

namespace AirCode.Views;

public partial class HostSetupDialog : Window
{
    public string NetworkName { get; private set; } = "AirCode-Classroom";
    public string Password { get; private set; } = "aircode2026";

    private bool _showingPassword;

    public HostSetupDialog()
    {
        InitializeComponent();
        NameBox.Text = "AirCode-Classroom";
        PassBox.Password = "aircode2026";
        PassText.Text = "aircode2026";
    }

    private void TogglePass_Click(object s, RoutedEventArgs e)
    {
        _showingPassword = !_showingPassword;

        if (_showingPassword)
        {
            PassText.Text = PassBox.Password;
            PassBox.Visibility = Visibility.Collapsed;
            PassText.Visibility = Visibility.Visible;
            TogglePassIcon.Text = "🙈";
        }
        else
        {
            PassBox.Password = PassText.Text;
            PassText.Visibility = Visibility.Collapsed;
            PassBox.Visibility = Visibility.Visible;
            TogglePassIcon.Text = "👁";
        }
    }

    private void Start_Click(object s, RoutedEventArgs e)
    {
        NetworkName = NameBox.Text.Trim();
        Password = _showingPassword ? PassText.Text : PassBox.Password;

        if (string.IsNullOrEmpty(NetworkName))
        {
            MessageBox.Show("Please enter a network name.", "AirCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Password.Length < 8)
        {
            MessageBox.Show("Password must be at least 8 characters.", "AirCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
