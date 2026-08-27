using System.Windows;
using System.Windows.Controls;
using AirCode.Models;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class MembersPage : UserControl
{
    private MainViewModel? _vm;

    public MembersPage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        MemberList.ItemsSource = vm.Members;
        vm.Members.CollectionChanged += (s, e) => UpdateCount();
        UpdateCount();
    }

    private void UpdateCount()
        => Dispatcher.InvokeAsync(() => CountText.Text = $"{_vm?.Members.Count ?? 0} Online");

    private void Message_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is Member m && _vm != null)
        {
            _vm.SelectedMember = m;
            var win = Window.GetWindow(this) as MainWindow;
            win?.Nav_Chat(s, e);
        }
    }

    private async void SendFile_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is Member m && _vm != null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Send file to {m.DisplayName}" };
            if (dlg.ShowDialog() == true)
                await _vm.OfferFileAsync(m, dlg.FileName);
        }
    }
}
