using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirCode.Models;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class ChatPage : UserControl
{
    private MainViewModel? _vm;
    private string? _activePeerId; // null = group chat

    public ChatPage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;

        // Group chat thread button
        RebuildThreadList();

        vm.Members.CollectionChanged += (s, e) => Dispatcher.InvokeAsync(RebuildThreadList);
        vm.GroupMessages.CollectionChanged += (s, e) =>
        {
            if (_activePeerId == null) ScrollToBottom();
        };

        ShowGroupChat();
    }

    private void RebuildThreadList()
    {
        if (_vm == null) return;
        ThreadList.Children.Clear();

        // Group chat button
        var groupBtn = MakeThreadButton("# Classroom Chat", null);
        ThreadList.Children.Add(groupBtn);

        // Per-member DM buttons
        foreach (var m in _vm.Members)
        {
            if (m.Id == _vm.MyId || m.IsHost && _vm.IsHost) continue;
            var btn = MakeThreadButton($"@ {m.DisplayName}", m.Id);
            ThreadList.Children.Add(btn);
        }
    }

    private Button MakeThreadButton(string label, string? peerId)
    {
        var btn = new Button
        {
            Content = label,
            Style = (Style)FindResource("NavButton"),
            Foreground = peerId == _activePeerId
                ? System.Windows.Media.Brushes.White
                : (System.Windows.Media.Brush)FindResource("SidebarTextBrush"),
            Tag = peerId
        };
        btn.Click += (s, e) =>
        {
            _activePeerId = peerId;
            if (peerId == null) ShowGroupChat();
            else ShowDirectChat(peerId);
            RebuildThreadList();
        };
        return btn;
    }

    private void ShowGroupChat()
    {
        _activePeerId = null;
        ChatHeaderText.Text = "Classroom Chat";
        MessageList.ItemsSource = _vm?.GroupMessages;
        ScrollToBottom();
    }

    private void ShowDirectChat(string peerId)
    {
        if (_vm == null) return;
        _activePeerId = peerId;
        var m = _vm.Members.FirstOrDefault(x => x.Id == peerId);
        ChatHeaderText.Text = m?.DisplayName ?? "Direct Message";

        if (!_vm.DirectThreads.TryGetValue(peerId, out var thread))
        {
            thread = new ObservableCollection<ChatMessage>();
            _vm.DirectThreads[peerId] = thread;
        }
        MessageList.ItemsSource = thread;
        ScrollToBottom();
    }

    private void ScrollToBottom()
        => Dispatcher.InvokeAsync(() => MsgScroll.ScrollToBottom());

    private async void Send_Click(object s, RoutedEventArgs e) => await SendMessage();

    private async void Input_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
        {
            e.Handled = true;
            await SendMessage();
        }
    }

    private async System.Threading.Tasks.Task SendMessage()
    {
        if (_vm == null) return;
        var text = MessageInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
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
}
