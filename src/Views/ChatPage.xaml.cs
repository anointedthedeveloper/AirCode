using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AirCode.Models;
using AirCode.Services;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class ChatPage : UserControl
{
    private MainViewModel? _vm;
    private string? _activePeerId; // null = group chat
    private INotifyCollectionChanged? _watching;

    public ChatPage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;

        vm.Members.CollectionChanged += (s, e) => Dispatcher.InvokeAsync(RebuildThreadList);
        vm.UnreadChanged += () => Dispatcher.InvokeAsync(RebuildThreadList);
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage) && vm.CurrentPage == AppPage.Chat)
                Dispatcher.InvokeAsync(OnShown);
        };

        ShowGroupChat();
    }

    /// <summary>Called whenever the Chat page becomes the visible page.</summary>
    private void OnShown()
    {
        if (_vm == null) return;

        // "Message" clicked on the Members page → open that DM
        var target = _vm.SelectedMember;
        if (target != null)
        {
            _vm.SelectedMember = null;
            if (target.Id != _vm.MyId) { ShowDirectChat(target.Id); return; }
        }

        _vm.ActiveChatPeer = _activePeerId;
        _vm.MarkRead(_activePeerId);
        RebuildThreadList();
        MessageInput.Focus();
        ScrollToBottom();
    }

    // ── Thread list ───────────────────────────────────────────────────────────

    private void RebuildThreadList()
    {
        if (_vm == null) return;
        ThreadList.Children.Clear();

        ThreadList.Children.Add(MakeThreadRow("# Classroom Chat", null));

        foreach (var m in _vm.Members)
        {
            if (m.Id == _vm.MyId) continue;
            ThreadList.Children.Add(MakeThreadRow(m.DisplayName + (m.IsHost ? "  (host)" : ""), m.Id));
        }
    }

    private Border MakeThreadRow(string label, string? peerId)
    {
        bool active = peerId == _activePeerId;
        int unread = active ? 0 : (_vm?.UnreadFor(peerId) ?? 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = active || unread > 0 ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(active ? "PrimaryBrush" : "TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (unread > 0)
        {
            var badge = new Border
            {
                Background = (Brush)FindResource("PrimaryBrush"),
                CornerRadius = new CornerRadius(9),
                MinWidth = 18, Height = 18,
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = unread > 99 ? "99+" : unread.ToString(),
                    FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(7),
            Cursor = Cursors.Hand,
            Background = active ? (Brush)FindResource("BgBrush") : Brushes.Transparent,
            Tag = peerId
        };
        row.MouseEnter += (s, e) => { if (peerId != _activePeerId) row.Background = (Brush)FindResource("BgBrush"); };
        row.MouseLeave += (s, e) => { if (peerId != _activePeerId) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (s, e) =>
        {
            if (peerId == null) ShowGroupChat(); else ShowDirectChat(peerId);
        };
        return row;
    }

    // ── Showing a conversation ────────────────────────────────────────────────

    private void ShowGroupChat()
    {
        _activePeerId = null;
        ChatHeaderText.Text = "Classroom Chat";
        ChatSubText.Text = "Everyone on the network";
        BindMessages(_vm?.GroupMessages);
        Activated();
    }

    private void ShowDirectChat(string peerId)
    {
        if (_vm == null) return;
        _activePeerId = peerId;
        var m = _vm.Members.FirstOrDefault(x => x.Id == peerId);
        ChatHeaderText.Text = m?.DisplayName ?? "Direct Message";
        ChatSubText.Text = "Private conversation";
        BindMessages(_vm.GetThread(peerId));
        Activated();
    }

    private void Activated()
    {
        if (_vm == null) return;
        _vm.ActiveChatPeer = _activePeerId;
        _vm.MarkRead(_activePeerId);
        RebuildThreadList();
        MessageInput.Focus();
    }

    private void BindMessages(ObservableCollection<ChatMessage>? source)
    {
        if (_watching != null) _watching.CollectionChanged -= OnMessagesChanged;
        MessageList.ItemsSource = source;
        _watching = source;
        if (_watching != null) _watching.CollectionChanged += OnMessagesChanged;
        UpdateEmpty();
        ScrollToBottom();
    }

    private void OnMessagesChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmpty();
        ScrollToBottom();
        // A message that lands in the thread we are looking at is already "read"
        if (_vm != null && IsVisible && e.Action == NotifyCollectionChangedAction.Add)
            _vm.MarkRead(_activePeerId);
    }

    private void UpdateEmpty()
        => EmptyText.Visibility = (MessageList.Items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

    private void ScrollToBottom()
        => Dispatcher.InvokeAsync(() => MsgScroll.ScrollToBottom(),
            System.Windows.Threading.DispatcherPriority.Background);

    // ── Sending ───────────────────────────────────────────────────────────────

    private async void Send_Click(object s, RoutedEventArgs e) => await SendMessage();

    private async void Input_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            e.Handled = true;
            await SendMessage();
        }
    }

    private async Task SendMessage()
    {
        if (_vm == null) return;
        var text = MessageInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (!_vm.IsConnected)
        {
            NotificationService.Instance.Show("Not connected",
                "Connect to an AirCode network to send messages.", NotificationKind.Warning);
            return;
        }
        MessageInput.Clear();

        if (_activePeerId == null)
            await _vm.SendGroupMessageAsync(text);
        else
        {
            var m = _vm.Members.FirstOrDefault(x => x.Id == _activePeerId);
            await _vm.SendDirectMessageAsync(_activePeerId, m?.DisplayName ?? "", text);
        }
        ScrollToBottom();
    }

    private void ClearHistory_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var what = _activePeerId == null ? "the group chat" : $"this conversation";
        if (MessageBox.Show($"Delete the saved history for {what}?", "Clear history",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.ClearChatHistory(_activePeerId);
            UpdateEmpty();
        }
    }
}
