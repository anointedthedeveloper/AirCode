using System.Windows;

namespace AirCode.Views;

public partial class SetupWindow : Window
{
    public string ChosenName { get; private set; }

    public SetupWindow(string defaultName)
    {
        InitializeComponent();
        ChosenName = defaultName;
        NameBox.Text = defaultName;
        NameBox.SelectAll();
        NameBox.Focus();
        NameBox.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Confirm();
        };
    }

    private void Continue_Click(object s, RoutedEventArgs e) => Confirm();

    private void Skip_Click(object s, RoutedEventArgs e)
    {
        ChosenName = Environment.MachineName;
        DialogResult = true;
    }

    private void Confirm()
    {
        var name = NameBox.Text.Trim();
        ChosenName = string.IsNullOrEmpty(name) ? Environment.MachineName : name;
        DialogResult = true;
    }
}
