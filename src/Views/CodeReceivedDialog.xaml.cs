using System.Windows;

namespace AirCode.Views;

public partial class CodeReceivedDialog : Window
{
    public CodeReceivedDialog(string senderName, string lang, string code)
    {
        InitializeComponent();
        TitleText.Text = $"Code from {senderName}";
        SubText.Text = $"Language: {lang}";
        CodeBox.Text = code;
    }

    private void Copy_Click(object s, RoutedEventArgs e)
    {
        Clipboard.SetText(CodeBox.Text);
        MessageBox.Show("Copied to clipboard.", "AirCode", MessageBoxButton.OK);
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
