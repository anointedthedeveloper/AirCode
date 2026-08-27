using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using AirCode.Models;
using AirCode.Services;
using Newtonsoft.Json;

namespace AirCode.ViewModels;

public enum ConnectionState { Disconnected, Connecting, ConnectedAsClient, ConnectedAsHost }
public enum AppPage { Home, Members, Chat, Files, Code, Transfers, Logs, Settings }

public class MainViewModel : INotifyPropertyChanged
{
    // ── Services ──────────────────────────────────────────────────────────────
    public readonly DatabaseService Db;
    private readonly DiscoveryService _discovery = new();
    private WebSocketServer? _server;
    private WebSocketClient? _client;
    private readonly FileTransferService _fileTransfer = new();
    private readonly HotspotService _hotspot = new();
    private CancellationTokenSource? _reconnectCts;

    // ── State ─────────────────────────────────────────────────────────────────
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private AppPage _currentPage = AppPage.Home;
    private AppSettings _settings;
    private Member? _selectedMember;
    private bool _isDarkMode;

    public ObservableCollection<Member> Members { get; } = new();
    public ObservableCollection<ChatMessage> GroupMessages { get; } = new();
    public ObservableCollection<FileTransfer> Transfers { get; } = new();
    public ObservableCollection<ActivityItem> RecentActivity { get; } = new();

    // Direct message threads keyed by peer ID
    public Dictionary<string, ObservableCollection<ChatMessage>> DirectThreads { get; } = new();

    public string MyId => IsHost ? "host" : (_client?.MyId ?? "");
    public bool IsHost => _connectionState == ConnectionState.ConnectedAsHost;
    public string? HostIp { get; private set; }
    public int OnlineMemberCount => Members.Count(m => m.IsOnline);

    // ── Properties ────────────────────────────────────────────────────────────

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            _connectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionStatusText));
            OnPropertyChanged(nameof(ConnectionStatusDetail));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsHost));
        }
    }

    public bool IsConnected => _connectionState is ConnectionState.ConnectedAsClient
                                                  or ConnectionState.ConnectedAsHost;

    public string ConnectionStatusText => _connectionState switch
    {
        ConnectionState.ConnectedAsHost => "Hosting",
        ConnectionState.ConnectedAsClient => "Connected",
        ConnectionState.Connecting => "Connecting…",
        _ => "Disconnected"
    };

    public string ConnectionStatusDetail => _connectionState switch
    {
        ConnectionState.ConnectedAsHost => $"AirCode Host · {OnlineMemberCount} member{(OnlineMemberCount == 1 ? "" : "s")}",
        ConnectionState.ConnectedAsClient => $"{OnlineMemberCount} member{(OnlineMemberCount == 1 ? "" : "s")} online",
        ConnectionState.Connecting => "Searching for AirCode Host…",
        _ => "Not connected to any AirCode network"
    };

    public AppPage CurrentPage
    {
        get => _currentPage;
        set { _currentPage = value; OnPropertyChanged(); }
    }

    public AppSettings Settings
    {
        get => _settings;
        set { _settings = value; OnPropertyChanged(); }
    }

    public Member? SelectedMember
    {
        get => _selectedMember;
        set { _selectedMember = value; OnPropertyChanged(); }
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set { _isDarkMode = value; OnPropertyChanged(); }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel()
    {
        Db = new DatabaseService();
        _settings = Db.LoadSettings();
        _isDarkMode = _settings.DarkMode;

        _fileTransfer.TransferCompleted += OnTransferCompleted;
        _fileTransfer.TransferFailed += OnTransferFailed;
        _fileTransfer.TransferProgress += t =>
            App.Current.Dispatcher.InvokeAsync(() =>
                NotificationService.Instance.Show("Transfer", $"{t.FileName} {t.Progress:F0}%", NotificationKind.Info));

        // Load history
        foreach (var msg in Db.LoadGroupChatHistory())
            GroupMessages.Add(msg);
        foreach (var t in Db.LoadTransferHistory())
            Transfers.Add(t);
    }

    // ── Host Mode ─────────────────────────────────────────────────────────────

    public async Task<(bool success, string message)> StartHostAsync(string ssid, string password)
    {
        var log = LogService.Instance;
        log.Info("Host", "StartHostAsync called");
        ConnectionState = ConnectionState.Connecting;

        string hotspotDetail = "";

        // Attempt hotspot (non-fatal)
        if (!string.IsNullOrEmpty(ssid))
        {
            var (result, detail) = await _hotspot.StartHotspotAsync(ssid, password);
            hotspotDetail = detail;

            if (result == HotspotResult.NoAdapter)
            {
                log.Error("Host", detail);
                ConnectionState = ConnectionState.Disconnected;
                return (false, detail);
            }

            if (result != HotspotResult.Success)
                log.Warn("Host", detail);
        }

        // Register URL reservation (best-effort, needs elevation)
        await TryReserveUrlAsync();

        var ip = HotspotService.GetBestLocalIp();
        HostIp = ip;
        log.Info("Host", $"Server will bind to {ip}:{WebSocketServer.WsPort}");

        _server = new WebSocketServer();
        _server.ClientJoined    += OnClientJoined;
        _server.ClientLeft      += OnClientLeft;
        _server.MessageReceived += OnServerMessageReceived;

        try
        {
            await _server.StartAsync(ip);
            log.Success("Host", $"WebSocket server started on {ip}:{WebSocketServer.WsPort}");
        }
        catch (Exception ex)
        {
            log.Error("Host", $"Server failed to start: {ex.Message}");
            ConnectionState = ConnectionState.Disconnected;
            return (false, $"Could not start server: {ex.Message}");
        }

        _discovery.StartHostBeacon(WebSocketServer.WsPort);
        log.Info("Host", "UDP discovery beacon started");

        var selfMember = new Member
        {
            Id          = "host",
            DisplayName = _settings.DisplayName,
            DeviceName  = Environment.MachineName,
            IsHost      = true
        };
        App.Current.Dispatcher.Invoke(() => Members.Add(selfMember));

        ConnectionState = ConnectionState.ConnectedAsHost;
        AddActivity($"Network started · {ip}");
        OnPropertyChanged(nameof(MyId));

        var msg = string.IsNullOrEmpty(hotspotDetail)
            ? $"AirCode running on {ip}"
            : $"AirCode running on {ip}\n{hotspotDetail}";

        return (true, msg);
    }

    private static async Task TryReserveUrlAsync()
    {
        var log = LogService.Instance;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "netsh",
                $"http add urlacl url=http://+:{WebSocketServer.WsPort}/ws/ user=Everyone")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            log.Debug("Host", $"URL ACL reservation exit code: {p.ExitCode}");
        }
        catch (Exception ex)
        {
            log.Warn("Host", $"URL ACL reservation failed (non-fatal): {ex.Message}");
        }
    }

    // ── Client Mode ───────────────────────────────────────────────────────────

    public async Task ConnectAsync(string? manualIp = null)
    {
        var log = LogService.Instance;
        log.Info("Client", manualIp != null ? $"Connecting to {manualIp}" : "Auto-discovering host…");
        ConnectionState = ConnectionState.Connecting;

        string? ip = manualIp;
        int port = WebSocketServer.WsPort;

        if (ip == null)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                log.Debug("Client", $"Discovery attempt {attempt + 1}/5…");
                var found = await _discovery.DiscoverHostAsync(TimeSpan.FromSeconds(3));
                if (found.HasValue)
                {
                    ip = found.Value.ip;
                    port = found.Value.port;
                    log.Success("Client", $"Host found at {ip}:{port}");
                    break;
                }
                await Task.Delay(1000);
            }
        }

        if (ip == null)
        {
            log.Warn("Client", "Host discovery failed — no AirCode host found on this network.");
            ConnectionState = ConnectionState.Disconnected;
            return;
        }

        _client = new WebSocketClient();
        _client.MessageReceived += OnClientMessageReceived;
        _client.Disconnected    += OnClientDisconnected;

        log.Info("Client", $"Connecting WebSocket to {ip}:{port}…");
        var ok = await _client.ConnectAsync(ip, port, _settings.DisplayName, Environment.MachineName);

        if (!ok)
        {
            log.Error("Client", $"WebSocket connection to {ip}:{port} failed.");
            ConnectionState = ConnectionState.Disconnected;
            return;
        }

        HostIp = ip;
        ConnectionState = ConnectionState.ConnectedAsClient;
        OnPropertyChanged(nameof(MyId));
        log.Success("Client", $"Connected to AirCode host at {ip}");
        AddActivity("Connected to AirCode network");
    }

    public async Task DisconnectAsync()
    {
        LogService.Instance.Info("Host", "Disconnecting…");
        _reconnectCts?.Cancel();
        _discovery.Stop();
        if (_server != null) { _server.Stop(); _server.Dispose(); _server = null; }
        if (_client != null) { await _client.DisconnectAsync(); _client.Dispose(); _client = null; }
        if (_hotspot.IsHotspotActive) await _hotspot.StopAsync();
        App.Current.Dispatcher.Invoke(() => Members.Clear());
        ConnectionState = ConnectionState.Disconnected;
        LogService.Instance.Info("Host", "Disconnected.");
    }

    // ── Reconnect ─────────────────────────────────────────────────────────────

    private async void OnClientDisconnected()
    {
        if (ConnectionState == ConnectionState.Disconnected) return;
        App.Current.Dispatcher.Invoke(() =>
        {
            ConnectionState = ConnectionState.Connecting;
            Members.Clear();
            NotificationService.Instance.Show("AirCode", "Connection lost — reconnecting…", NotificationKind.Warning);
        });

        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        await Task.Delay(2000, token);
        while (!token.IsCancellationRequested)
        {
            var found = await _discovery.DiscoverHostAsync(TimeSpan.FromSeconds(3));
            if (found.HasValue)
            {
                await App.Current.Dispatcher.InvokeAsync(() => ConnectAsync(found.Value.ip));
                break;
            }
            await Task.Delay(3000, token);
        }
    }

    // ── Message Routing ───────────────────────────────────────────────────────

    public async Task SendGroupMessageAsync(string text)
    {
        var msg = new ChatMessage
        {
            SenderId = MyId,
            SenderName = _settings.DisplayName,
            Content = text,
            IsOwn = true
        };
        App.Current.Dispatcher.Invoke(() => GroupMessages.Add(msg));
        Db.SaveChatMessage(msg);
        AddActivity($"You: {Truncate(text, 40)}");

        var net = new NetworkMessage
        {
            Type = MessageType.ChatMessage,
            SenderName = _settings.DisplayName,
            Payload = text
        };
        await SendMessageAsync(net);
    }

    public async Task SendDirectMessageAsync(string recipientId, string recipientName, string text)
    {
        var msg = new ChatMessage
        {
            SenderId = MyId,
            SenderName = _settings.DisplayName,
            RecipientId = recipientId,
            Content = text,
            IsOwn = true
        };
        EnsureThread(recipientId);
        App.Current.Dispatcher.Invoke(() => DirectThreads[recipientId].Add(msg));

        var net = new NetworkMessage
        {
            Type = MessageType.DirectMessage,
            SenderName = _settings.DisplayName,
            RecipientId = recipientId,
            Payload = text
        };
        await SendMessageAsync(net);
    }

    public async Task SendCodeAsync(string recipientId, string language, string code)
    {
        var net = new NetworkMessage
        {
            Type = MessageType.CodeShare,
            SenderName = _settings.DisplayName,
            RecipientId = recipientId,
            Payload = JsonConvert.SerializeObject(new { language, code })
        };
        await SendMessageAsync(net);
        AddActivity($"You shared a {language} snippet");
    }

    public async Task<FileTransfer?> OfferFileAsync(Member target, string filePath)
    {
        var fi = new System.IO.FileInfo(filePath);
        if (!fi.Exists) return null;
        if (fi.Length > 2L * 1024 * 1024 * 1024)
        {
            NotificationService.Instance.Show("File too large", "Maximum file size is 2 GB", NotificationKind.Warning);
            return null;
        }

        var transfer = new FileTransfer
        {
            FileName = fi.Name,
            FileSize = fi.Length,
            PeerId = target.Id,
            PeerName = target.DisplayName,
            Direction = TransferDirection.Sending
        };

        // Start the TCP sender and get the port
        var tcpPort = await _fileTransfer.BeginSendAsync(transfer, filePath);

        App.Current.Dispatcher.Invoke(() => Transfers.Insert(0, transfer));

        // Send offer to peer
        var net = new NetworkMessage
        {
            Type = MessageType.FileOffer,
            SenderName = _settings.DisplayName,
            RecipientId = target.Id,
            Payload = JsonConvert.SerializeObject(new
            {
                transferId = transfer.Id,
                fileName = fi.Name,
                fileSize = fi.Length,
                tcpPort,
                senderIp = HostIp ?? HotspotService.GetBestLocalIp()
            })
        };
        await SendMessageAsync(net);
        AddActivity($"You → {target.DisplayName}: {fi.Name}");
        return transfer;
    }

    // ── Incoming message handlers ─────────────────────────────────────────────

    private void OnClientMessageReceived(NetworkMessage msg)
    {
        App.Current.Dispatcher.InvokeAsync(() => HandleIncomingMessage(msg));
    }

    private void OnServerMessageReceived(NetworkMessage msg)
    {
        // On host: messages that are addressed to us (host) or broadcasts
        if (string.IsNullOrEmpty(msg.RecipientId) || msg.RecipientId == "host")
            App.Current.Dispatcher.InvokeAsync(() => HandleIncomingMessage(msg));
    }

    private void HandleIncomingMessage(NetworkMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.MemberList:
                var memberDtos = JsonConvert.DeserializeObject<List<MemberDto>>(msg.Payload) ?? new();
                Members.Clear();
                foreach (var dto in memberDtos)
                    Members.Add(new Member { Id = dto.Id, DisplayName = dto.DisplayName, DeviceName = dto.DeviceName, IsHost = dto.IsHost });
                OnPropertyChanged(nameof(OnlineMemberCount));
                OnPropertyChanged(nameof(ConnectionStatusDetail));
                break;

            case MessageType.MemberJoined:
                var jDto = JsonConvert.DeserializeObject<MemberDto>(msg.Payload);
                if (jDto != null && !Members.Any(m => m.Id == jDto.Id))
                {
                    Members.Add(new Member { Id = jDto.Id, DisplayName = jDto.DisplayName, DeviceName = jDto.DeviceName, IsHost = jDto.IsHost });
                    AddActivity($"{jDto.DisplayName} joined the network");
                    NotificationService.Instance.Show("AirCode", $"{jDto.DisplayName} joined", NotificationKind.Info);
                    OnPropertyChanged(nameof(OnlineMemberCount));
                    OnPropertyChanged(nameof(ConnectionStatusDetail));
                }
                break;

            case MessageType.MemberLeft:
                var leftId = msg.Payload;
                var leaving = Members.FirstOrDefault(m => m.Id == leftId);
                if (leaving != null)
                {
                    AddActivity($"{leaving.DisplayName} left");
                    Members.Remove(leaving);
                    OnPropertyChanged(nameof(OnlineMemberCount));
                    OnPropertyChanged(nameof(ConnectionStatusDetail));
                }
                break;

            case MessageType.ChatMessage:
                var chatMsg = new ChatMessage
                {
                    SenderId = msg.SenderId,
                    SenderName = msg.SenderName,
                    Content = msg.Payload,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp).LocalDateTime,
                    IsOwn = false
                };
                GroupMessages.Add(chatMsg);
                Db.SaveChatMessage(chatMsg);
                AddActivity($"{msg.SenderName}: {Truncate(msg.Payload, 40)}");
                if (CurrentPage != AppPage.Chat)
                    NotificationService.Instance.Show(msg.SenderName, msg.Payload, NotificationKind.Info);
                break;

            case MessageType.DirectMessage:
                var dm = new ChatMessage
                {
                    SenderId = msg.SenderId,
                    SenderName = msg.SenderName,
                    RecipientId = msg.RecipientId,
                    Content = msg.Payload,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp).LocalDateTime,
                    IsOwn = false
                };
                EnsureThread(msg.SenderId);
                DirectThreads[msg.SenderId].Add(dm);
                NotificationService.Instance.Show($"DM from {msg.SenderName}", msg.Payload, NotificationKind.Info);
                break;

            case MessageType.CodeShare:
                var codeData = JsonConvert.DeserializeObject<CodeShareData>(msg.Payload);
                if (codeData != null)
                {
                    AddActivity($"{msg.SenderName} shared a {codeData.Language} snippet");
                    NotificationService.Instance.Show($"Code from {msg.SenderName}", $"{codeData.Language} snippet received", NotificationKind.Info);
                    CodeReceived?.Invoke(msg.SenderName, codeData.Language, codeData.Code);
                }
                break;

            case MessageType.FileOffer:
                var offer = JsonConvert.DeserializeObject<FileOfferData>(msg.Payload);
                if (offer != null)
                {
                    var incoming = new FileTransfer
                    {
                        Id = offer.TransferId,
                        FileName = offer.FileName,
                        FileSize = offer.FileSize,
                        PeerId = msg.SenderId,
                        PeerName = msg.SenderName,
                        Direction = TransferDirection.Receiving,
                        Status = TransferStatus.Pending
                    };
                    Transfers.Insert(0, incoming);
                    FileOfferReceived?.Invoke(incoming, msg.SenderId, offer.TcpPort, offer.SenderIp);
                }
                break;

            case MessageType.NameChange:
                var changing = Members.FirstOrDefault(m => m.Id == msg.SenderId);
                if (changing != null) changing.DisplayName = msg.Payload;
                break;
        }
    }

    // ── Events for the UI ─────────────────────────────────────────────────────

    public event Action<string, string, string>? CodeReceived; // senderName, lang, code
    public event Action<FileTransfer, string, int, string>? FileOfferReceived; // transfer, senderIp, tcpPort, senderIp

    // ── File Accept / Decline ─────────────────────────────────────────────────

    public async Task AcceptFileAsync(FileTransfer transfer, string senderIp, int tcpPort)
    {
        var savePath = GetUniqueSavePath(transfer.FileName);
        transfer.SavePath = savePath;

        await _fileTransfer.ReceiveFileAsync(transfer, senderIp, tcpPort, savePath);
        Db.SaveTransfer(transfer);
    }

    public void DeclineFile(FileTransfer transfer)
    {
        transfer.Status = TransferStatus.Declined;
        Db.SaveTransfer(transfer);
    }

    // ── Host event handlers ───────────────────────────────────────────────────

    private void OnClientJoined(Member m)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            if (!Members.Any(x => x.Id == m.Id))
            {
                Members.Add(m);
                AddActivity($"{m.DisplayName} joined");
                OnPropertyChanged(nameof(OnlineMemberCount));
                OnPropertyChanged(nameof(ConnectionStatusDetail));
                NotificationService.Instance.Show("AirCode", $"{m.DisplayName} joined", NotificationKind.Info);
            }
        });
    }

    private void OnClientLeft(string id)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var m = Members.FirstOrDefault(x => x.Id == id);
            if (m != null)
            {
                AddActivity($"{m.DisplayName} disconnected");
                Members.Remove(m);
                OnPropertyChanged(nameof(OnlineMemberCount));
                OnPropertyChanged(nameof(ConnectionStatusDetail));
            }
        });
    }

    // ── Transfer callbacks ────────────────────────────────────────────────────

    private void OnTransferCompleted(FileTransfer t)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Db.SaveTransfer(t);
            var verb = t.Direction == TransferDirection.Sending ? "sent" : "received";
            AddActivity($"{t.FileName} {verb}");
            NotificationService.Instance.Show("Transfer complete", $"{t.FileName}", NotificationKind.Success);
        });
    }

    private void OnTransferFailed(FileTransfer t)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Db.SaveTransfer(t);
            NotificationService.Instance.Show("Transfer failed", t.FileName, NotificationKind.Error);
        });
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    public async Task UpdateDisplayNameAsync(string newName)
    {
        _settings.DisplayName = newName;
        Db.SaveSettings(_settings);

        if (IsConnected)
        {
            await SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.NameChange,
                SenderName = newName,
                Payload = newName
            });
        }
    }

    public void SaveSettings()
    {
        _settings.DarkMode = _isDarkMode;
        Db.SaveSettings(_settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SendMessageAsync(NetworkMessage msg)
    {
        if (IsHost && _server != null)
        {
            // On host: broadcast or route directly
            if (!string.IsNullOrEmpty(msg.RecipientId))
            {
                msg.SenderId = "host";
                msg.SenderName = _settings.DisplayName;
                await _server.BroadcastAsync(msg, exclude: null);
            }
            else
                await _server.BroadcastAsync(msg, exclude: null);
        }
        else if (_client != null)
            await _client.SendAsync(msg);
    }

    private void EnsureThread(string peerId)
    {
        if (!DirectThreads.ContainsKey(peerId))
            DirectThreads[peerId] = new ObservableCollection<ChatMessage>();
    }

    private string GetUniqueSavePath(string fileName)
    {
        Directory.CreateDirectory(_settings.DownloadFolder);
        var path = Path.Combine(_settings.DownloadFolder, fileName);
        if (!File.Exists(path)) return path;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        int i = 1;
        while (File.Exists(path))
            path = Path.Combine(_settings.DownloadFolder, $"{name} ({i++}){ext}");
        return path;
    }

    private void AddActivity(string text)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            RecentActivity.Insert(0, new ActivityItem { Text = text, Time = DateTime.Now });
            while (RecentActivity.Count > 20) RecentActivity.RemoveAt(RecentActivity.Count - 1);
        });
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // ── DTOs ──────────────────────────────────────────────────────────────────
    private record MemberDto(string Id, string DisplayName, string DeviceName, bool IsHost);
    private record CodeShareData(string Language, string Code);
    private record FileOfferData(string TransferId, string FileName, long FileSize, int TcpPort, string SenderIp);
}

public class ActivityItem
{
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }
    public string TimeText => Time.ToString("HH:mm");
}
