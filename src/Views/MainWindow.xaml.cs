using System.Windows;
using System.Windows.Controls;
using AirCode.Models;
using AirCode.Services;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class MainWindow : Window
{
    public MainViewModel VM { get; }

    // Track which nav button is currently active
    private Button? _activeNavBtn;

    public MainWindow()
    {
        InitializeComponent();
        VM = new MainViewModel();
        DataContext = VM;

        HomePage.Initialize(VM);
        MembersPage.Initialize(VM);
        ChatPage.Initialize(VM);
        FilesPage.Initialize(VM);
        CodePage.Initialize(VM);
        TransfersPage.Initialize(VM);
        SettingsPage.Initialize(VM);
        LogPage.Initialize();

        NotificationService.Instance.NotificationRequested += (title, msg, kind) =>
            ToastHost.Show(title, msg, kind);

        VM.FileOfferReceived += OnFileOfferReceived;

        // Set initial active nav
        SetActiveNav(NavHome);

        if (VM.Settings.FirstRun)
        {
            VM.Settings.FirstRun = false;
            VM.Db.SaveSettings(VM.Settings);
            Loaded += async (s, e) =>
            {
                var setup = new SetupWindow(VM.Settings.DisplayName) { Owner = this };
                if (setup.ShowDialog() == true)
                    await VM.UpdateDisplayNameAsync(setup.ChosenName);
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

    private void SetActiveNav(Button btn)
    {
        // Reset previous
        if (_activeNavBtn != null)
            _activeNavBtn.Style = (Style)FindResource("NavButton");

        // Activate new
        btn.Style = (Style)FindResource("NavButtonActive");
        _activeNavBtn = btn;
    }

    private void ShowPage(AppPage page, Button navBtn)
    {
        VM.CurrentPage = page;
        SetActiveNav(navBtn);

        HomePage.Visibility      = page == AppPage.Home      ? Visibility.Visible : Visibility.Collapsed;
        MembersPage.Visibility   = page == AppPage.Members   ? Visibility.Visible : Visibility.Collapsed;
        ChatPage.Visibility      = page == AppPage.Chat      ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility     = page == AppPage.Files     ? Visibility.Visible : Visibility.Collapsed;
        CodePage.Visibility      = page == AppPage.Code      ? Visibility.Visible : Visibility.Collapsed;
        TransfersPage.Visibility = page == AppPage.Transfers ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility       = page == AppPage.Logs      ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility  = page == AppPage.Settings  ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void Nav_Home(object s, RoutedEventArgs e)      => ShowPage(AppPage.Home,      NavHome);
    internal void Nav_Members(object s, RoutedEventArgs e)   => ShowPage(AppPage.Members,   NavMembers);
    internal void Nav_Chat(object s, RoutedEventArgs e)      => ShowPage(AppPage.Chat,       NavChat);
    internal void Nav_Files(object s, RoutedEventArgs e)     => ShowPage(AppPage.Files,      NavFiles);
    internal void Nav_Code(object s, RoutedEventArgs e)      => ShowPage(AppPage.Code,       NavCode);
    internal void Nav_Transfers(object s, RoutedEventArgs e) => ShowPage(AppPage.Transfers,  NavTransfers);
    internal void Nav_Logs(object s, RoutedEventArgs e)      => ShowPage(AppPage.Logs,       NavLogs);
    internal void Nav_Settings(object s, RoutedEventArgs e)  => ShowPage(AppPage.Settings,   NavSettings);

    protected override async void OnClosed(EventArgs e)
    {
        await VM.DisconnectAsync();
        base.OnClosed(e);
    }
}
