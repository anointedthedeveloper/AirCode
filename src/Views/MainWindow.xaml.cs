using System.Windows;
using AirCode.Models;
using AirCode.Services;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class MainWindow : Window
{
    public MainViewModel VM { get; }

    public MainWindow()
    {
        InitializeComponent();
        VM = new MainViewModel();
        DataContext = VM;

        // Wire up pages with shared VM
        HomePage.Initialize(VM);
        MembersPage.Initialize(VM);
        ChatPage.Initialize(VM);
        FilesPage.Initialize(VM);
        CodePage.Initialize(VM);
        TransfersPage.Initialize(VM);
        SettingsPage.Initialize(VM);

        // Toast notifications
        NotificationService.Instance.NotificationRequested += (title, msg, kind) =>
            ToastHost.Show(title, msg, kind);

        // File offer dialog
        VM.FileOfferReceived += OnFileOfferReceived;

        // Show setup if first run
        if (VM.Settings.FirstRun)
        {
            VM.Settings.FirstRun = false;
            VM.Db.SaveSettings(VM.Settings);
            Loaded += async (s, e) =>
            {
                var setup = new SetupWindow(VM.Settings.DisplayName) { Owner = this };
                if (setup.ShowDialog() == true)
                {
                    await VM.UpdateDisplayNameAsync(setup.ChosenName);
                }
            };
        }
    }

    private void OnFileOfferReceived(FileTransfer transfer, string senderIp, int tcpPort, string _)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            var dlg = new FileOfferDialog(transfer) { Owner = this };
            if (dlg.ShowDialog() == true)
                await VM.AcceptFileAsync(transfer, senderIp, tcpPort);
            else
                VM.DeclineFile(transfer);
        });
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void ShowPage(AppPage page)
    {
        VM.CurrentPage = page;
        HomePage.Visibility      = page == AppPage.Home      ? Visibility.Visible : Visibility.Collapsed;
        MembersPage.Visibility   = page == AppPage.Members   ? Visibility.Visible : Visibility.Collapsed;
        ChatPage.Visibility      = page == AppPage.Chat      ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility     = page == AppPage.Files     ? Visibility.Visible : Visibility.Collapsed;
        CodePage.Visibility      = page == AppPage.Code      ? Visibility.Visible : Visibility.Collapsed;
        TransfersPage.Visibility = page == AppPage.Transfers ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility  = page == AppPage.Settings  ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_Home(object s, RoutedEventArgs e)      => ShowPage(AppPage.Home);
    private void Nav_Members(object s, RoutedEventArgs e)   => ShowPage(AppPage.Members);
    private void Nav_Chat(object s, RoutedEventArgs e)      => ShowPage(AppPage.Chat);
    private void Nav_Files(object s, RoutedEventArgs e)     => ShowPage(AppPage.Files);
    private void Nav_Code(object s, RoutedEventArgs e)      => ShowPage(AppPage.Code);
    private void Nav_Transfers(object s, RoutedEventArgs e) => ShowPage(AppPage.Transfers);
    private void Nav_Settings(object s, RoutedEventArgs e)  => ShowPage(AppPage.Settings);

    protected override async void OnClosed(EventArgs e)
    {
        await VM.DisconnectAsync();
        base.OnClosed(e);
    }
}
