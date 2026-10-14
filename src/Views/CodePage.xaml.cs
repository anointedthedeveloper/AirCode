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

        // Inbox of everything received this session
        InboxList.ItemsSource = vm.ReceivedSnippets;
        vm.ReceivedSnippets.CollectionChanged += (s, e) => UpdateInbox();
        UpdateInbox();

        // Pop incoming snippets open (non-modal, so several can arrive without blocking the app)
        vm.CodeReceived += (senderName, lang, code) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                var dlg = new CodeReceivedDialog(senderName, lang, code)
                { Owner = Window.GetWindow(this) };
                dlg.Show();
            });
        };
    }

    private void UpdateInbox()
        => InboxPanel.Visibility = (_vm?.ReceivedSnippets.Count ?? 0) > 0
            ? Visibility.Visible : Visibility.Collapsed;

    private void OpenSnippet_Click(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: ReceivedSnippet sn })
            new CodeReceivedDialog(sn.SenderName, sn.Language, sn.Code)
            { Owner = Window.GetWindow(this) }.Show();
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
        if (!_vm.IsConnected)
        {
            Services.NotificationService.Instance.Show("Not connected",
                "Connect to an AirCode network first.", Services.NotificationKind.Warning);
            return;
        }

        var lang = LangCombo.SelectedItem?.ToString() ?? "Plain Text";
        var recipientId = RecipientCombo.SelectedItem is Member m ? m.Id : "";

        await _vm.SendCodeAsync(recipientId, lang, code);
        Services.NotificationService.Instance.Show("Code shared",
            $"{lang} snippet sent to {(RecipientCombo.SelectedItem is Member rm ? rm.DisplayName : "everyone")}",
            Services.NotificationKind.Success);
    }

    private void Clear_Click(object s, RoutedEventArgs e) => CodeBox.Clear();
}
