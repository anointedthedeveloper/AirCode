using System.Windows;
using System.Windows.Controls;
using AirCode.Models;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class CodePage : UserControl
{
    private MainViewModel? _vm;
    private static readonly string[] Languages =
    {
        "JavaScript", "TypeScript", "HTML", "CSS", "Java",
        "Python", "C#", "C++", "JSON", "SQL", "Bash", "Plain Text"
    };

    public CodePage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        foreach (var lang in Languages) LangCombo.Items.Add(lang);
        LangCombo.SelectedIndex = 0;

        vm.Members.CollectionChanged += (s, e) => Dispatcher.InvokeAsync(RebuildRecipients);
        RebuildRecipients();

        // Handle incoming code snippets
        vm.CodeReceived += (senderName, lang, code) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                var dlg = new CodeReceivedDialog(senderName, lang, code)
                { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            });
        };
    }

    private void RebuildRecipients()
    {
        if (_vm == null) return;
        RecipientCombo.Items.Clear();
        RecipientCombo.Items.Add("Everyone");
        foreach (var m in _vm.Members.Where(x => x.Id != _vm.MyId))
            RecipientCombo.Items.Add(m);
        RecipientCombo.SelectedIndex = 0;
    }

    private async void Share_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var code = CodeBox.Text.Trim();
        if (string.IsNullOrEmpty(code)) { MessageBox.Show("Please enter some code."); return; }

        var lang = LangCombo.SelectedItem?.ToString() ?? "Plain Text";
        var recipientId = RecipientCombo.SelectedItem is Member m ? m.Id : "";

        await _vm.SendCodeAsync(recipientId, lang, code);
        MessageBox.Show("Code shared successfully.", "AirCode",
            MessageBoxButton.OK, MessageBoxImage.None);
    }

    private void Clear_Click(object s, RoutedEventArgs e) => CodeBox.Clear();
}
