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
    private CancellationTokenSource? _connectCts;
    private string? _lastHostIp;
    private int _lastHostPort = WebSocketServer.WsPort;

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
    public ObservableCollection<ReceivedSnippet> ReceivedSnippets { get; } = new();

    // Direct message threads keyed by peer ID
    public Dictionary<string, ObservableCollection<ChatMessage>> DirectThreads { get; } = new();

    public string MyId => IsHost ? "host" : (_client?.MyId ?? _settings.ClientUid);
    public bool IsHost => _connectionState == ConnectionState.ConnectedAsHost;
    public string? HostIp { get; private set; }

    // Hotspot details shown on the Home page so classmates know what to join
    public bool   HotspotActive   { get; private set; }
    public string HotspotSsid     { get; private set; } = "";
    public string HotspotPassword { get; private set; } = "";

    // Chat state: which thread is on screen (null = group chat) and unread counters
    public string? ActiveChatPeer { get; set; }
    private readonly Dictionary<string, int> _unread = new();
    public int TotalUnread => _unread.Values.Sum();
    public int UnreadFor(string? peerId) => _unread.TryGetValue(peerId ?? "", out var n) ? n : 0;
    public event Action? UnreadChanged;
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

        // Make sure the install's identity is persisted (it is generated on first run)
        Db.SaveSettings(_settings);

        // Load history
        foreach (var msg in Db.LoadGroupChatHistory())
            GroupMessages.Add(msg);
        foreach (var t in Db.LoadTransferHistory())
        {
            // A transfer that was in flight when the app closed can never finish
            if (t.IsActive) { t.Status = TransferStatus.Cancelled; Db.SaveTransfer(t); }
            Transfers.Add(t);
        }
    }

    /// <summary>Synchronous teardown for app exit (stops servers and the hotspot we started).</summary>
    public void Shutdown()
    {
        try { _reconnectCts?.Cancel(); _connectCts?.Cancel(); } catch { }
        try { _discovery.Stop(); } catch { }
        try { _server?.Stop(); _server?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { Task.Run(() => _hotspot.StopAsync()).Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    // ── Host Mode ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts hosting. With <paramref name="useHotspot"/> the PC shares its Wi-Fi as a Windows
    /// hotspot (other devices join that hotspot); otherwise it hosts on the network it is already on.
    /// </summary>
    public async Task<(bool success, string message)> StartHostAsync(
        bool useHotspot = true, string ssid = "AirCode", string password = "aircode123")
    {
        var log = LogService.Instance;
        log.Info("Host", "StartHostAsync called");
        if (IsConnected || ConnectionState == ConnectionState.Connecting)
            return (false, "Already connected — disconnect first.");
        ConnectionState = ConnectionState.Connecting;

        string ip = "";
        string note = "";
        HotspotActive = false; HotspotSsid = ""; HotspotPassword = "";

        if (useHotspot)
        {
            var hs = await _hotspot.StartAsync(ssid, password);
            if (hs.Result is HotspotResult.Success or HotspotResult.AlreadyOn)
            {
                ip = string.IsNullOrEmpty(hs.Ip) ? "192.168.137.1" : hs.Ip;
                HotspotActive = true; HotspotSsid = hs.Ssid; HotspotPassword = hs.Passphrase;
            }
            else
            {
                log.Warn("Host", $"Hotspot unavailable: {hs.Message}");
                note = $"\n\nHotspot could not be started: {hs.Message}\nHosting on the current network instead.";
            }
        }

        if (string.IsNullOrEmpty(ip))
            ip = HotspotService.GetBestLocalIp();

        if (string.IsNullOrEmpty(ip))
        {
            log.Error("Host", "No network adapter available.");
            ConnectionState = ConnectionState.Disconnected;
            return (false, "No network detected.\n\nConnect to a Wi-Fi network first, then start hosting.");
        }

        HostIp = ip;
        log.Info("Host", $"Host IP: {ip}");

        // Let other devices through the Windows firewall, and make sure the listener may bind.
        await HotspotService.EnsureFirewallRuleAsync();
        await TryReserveUrlAsync();

        var selfMember = new Member
        {
            Id          = "host",
            DisplayName = _settings.DisplayName,
            DeviceName  = Environment.MachineName,
            IsHost      = true
        };

        _server = new WebSocketServer { HostMember = selfMember };
        _server.ClientJoined    += OnClientJoined;
        _server.ClientLeft      += OnClientLeft;
        _server.MessageReceived += OnServerMessageReceived;

        try
        {
            await _server.StartAsync(ip);
            log.Success("Host", $"Server listening on 0.0.0.0:{WebSocketServer.WsPort}  (advertised: {ip})");
        }
        catch (Exception ex)
        {
            log.Error("Host", $"Server failed: {ex.Message}");
            _server.Dispose(); _server = null;
            await _hotspot.StopAsync();
            HotspotActive = false;
            ConnectionState = ConnectionState.Disconnected;
            return (false, $"Could not start server: {ex.Message}");
        }

        _discovery.StartHostBeacon(WebSocketServer.WsPort);
        log.Info("Host", "UDP discovery beacon started");

        App.Current.Dispatcher.Invoke(() => { Members.Clear(); Members.Add(selfMember); });

        ConnectionState = ConnectionState.ConnectedAsHost;
        AddActivity($"Hosting on {ip}");
        OnPropertyChanged(nameof(MyId));
        OnPropertyChanged(nameof(HotspotActive));
        OnPropertyChanged(nameof(HotspotSsid));
        OnPropertyChanged(nameof(HotspotPassword));

        var msg = HotspotActive
            ? $"Hotspot \"{HotspotSsid}\" is on ({ip}).\nDevices join it, then open AirCode and tap Connect."
            : $"AirCode running on {ip}\nClients auto-discover via UDP — no IP entry needed.";
        return (true, msg + note);
    }

    private static async Task TryReserveUrlAsync()
    {
        try
        {
            await HotspotService.RunNetshAsync(
                $"http add urlacl url=http://+:{WebSocketServer.WsPort}/ user=Everyone");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("Host", $"URL ACL reservation failed (non-fatal): {ex.Message}");
        }
    }

    // ── Client Mode ───────────────────────────────────────────────────────────

    public async Task ConnectAsync(string? manualIp = null)
    {
        var log = LogService.Instance;
        if (IsConnected) return;
        log.Info("Client", manualIp != null
            ? $"Manual connect to {manualIp}"
            : "Auto-discovering host…");

        _reconnectCts?.Cancel();
        _connectCts?.Cancel();
        var cts = _connectCts = new CancellationTokenSource();
        var token = cts.Token;
        ConnectionState = ConnectionState.Connecting;

        _ = HotspotService.EnsureFirewallRuleAsync();   // so file transfers from peers can reach us

        bool ok = false;
        try
        {
            if (manualIp != null)
            {
                ok = await ConnectCoreAsync(manualIp.Trim(), WebSocketServer.WsPort, 8);
            }
            else
            {
                // Several probe rounds — Wi-Fi drops the first broadcast surprisingly often
                for (int attempt = 1; attempt <= 5 && !ok && !token.IsCancellationRequested; attempt++)
                {
                    log.Debug("Client", $"Discovery attempt {attempt}/5…");
                    var found = await _discovery.DiscoverHostAsync(TimeSpan.FromSeconds(2));
                    if (found.HasValue)
                        ok = await ConnectCoreAsync(found.Value.ip, found.Value.port, 8);
                }

                // Broadcast blocked (some hotspots/routers)? On a hotspot the gateway is the host.
                if (!ok && !token.IsCancellationRequested)
                {
                    var guesses = HotspotService.GetGatewayAddresses().ToList();
                    if (!guesses.Contains("192.168.137.1")) guesses.Add("192.168.137.1");
                    foreach (var g in guesses)
                    {
                        if (token.IsCancellationRequested) break;
                        log.Debug("Client", $"Trying gateway {g}…");
                        if (ok = await ConnectCoreAsync(g, WebSocketServer.WsPort, 3)) break;
                    }
                }
            }
        }
        catch (Exception ex) { log.Error("Client", $"Connect error: {ex.Message}"); }

        if (!ok || token.IsCancellationRequested)
        {
            if (ok) await DisconnectAsync();    // user cancelled while we were connecting
            else if (ConnectionState == ConnectionState.Connecting && !token.IsCancellationRequested)
            {
                log.Warn("Client", "No AirCode host found on this network.");
                ConnectionState = ConnectionState.Disconnected;
            }
        }
    }

    /// <summary>One connection attempt. On success the client is installed and state flips to connected.</summary>
    private async Task<bool> ConnectCoreAsync(string ip, int port, int timeoutSecs)
    {
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            LogService.Instance.Warn("Client", $"'{ip}' is not a valid IP address.");
            return false;
        }

        // Handlers go on first: the host sends the member list the moment we register
        var client = new WebSocketClient();
        client.MessageReceived += OnClientMessageReceived;
        client.Disconnected    += () => OnClientDisconnected(client);

        var ok = await client.ConnectAsync(ip, port, _settings.DisplayName,
            Environment.MachineName, _settings.ClientUid, timeoutSecs);
        if (!ok) { client.Dispose(); return false; }

        var old = _client;
        _client = client;
        old?.Dispose();

        HostIp = ip;
        _lastHostIp = ip;
        _lastHostPort = port;
        ConnectionState = ConnectionState.ConnectedAsClient;
        OnPropertyChanged(nameof(MyId));
        LogService.Instance.Success("Client", $"Connected to {ip}");
        AddActivity("Connected to AirCode network");
        return true;
    }

    public async Task DisconnectAsync()
    {
        LogService.Instance.Info("Host", "Disconnecting…");
        _reconnectCts?.Cancel();
        _connectCts?.Cancel();
        _discovery.Stop();
        if (_server != null) { _server.Stop(); _server.Dispose(); _server = null; }
        if (_client != null)
        {
            var c = _client; _client = null;
            await c.DisconnectAsync(); c.Dispose();
        }
        await _hotspot.StopAsync();
        HotspotActive = false; HotspotSsid = ""; HotspotPassword = "";
        OnPropertyChanged(nameof(HotspotActive));
        App.Current.Dispatcher.Invoke(() => Members.Clear());
        ConnectionState = ConnectionState.Disconnected;
        LogService.Instance.Info("Host", "Disconnected.");
    }

    // ── Reconnect ─────────────────────────────────────────────────────────────

    private void OnClientDisconnected(WebSocketClient who)
    {
        if (!ReferenceEquals(who, _client)) return;                 // stale client
        if (ConnectionState == ConnectionState.Disconnected) return;

        App.Current.Dispatcher.Invoke(() =>
        {
            ConnectionState = ConnectionState.Connecting;
            Members.Clear();
            NotificationService.Instance.Show("AirCode", "Connection lost — reconnecting…", NotificationKind.Warning);
        });

        _reconnectCts?.Cancel();
        var cts = _reconnectCts = new CancellationTokenSource();
        _ = Task.Run(() => ReconnectLoopAsync(cts.Token));
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1500, token);
            while (!token.IsCancellationRequested)
            {
                // Same host as before first (fast path), then full discovery (host may have a new IP)
                if (_lastHostIp != null && await ConnectCoreAsync(_lastHostIp, _lastHostPort, 4)) break;

                var found = await _discovery.DiscoverHostAsync(TimeSpan.FromSeconds(3));
                if (found.HasValue && await ConnectCoreAsync(found.Value.ip, found.Value.port, 6)) break;

                await Task.Delay(2000, token);
            }
            if (!token.IsCancellationRequested)
                NotificationService.Instance.Show("AirCode", "Reconnected", NotificationKind.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Instance.Error("Client", $"Reconnect error: {ex.Message}");
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
        var thread = GetThread(recipientId);
        App.Current.Dispatcher.Invoke(() => thread.Add(msg));
        Db.SaveChatMessage(msg);

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
        if (!IsConnected) return;
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

    /// <summary>The address peers should use to reach this PC for direct file transfers.</summary>
    private string GetMyAddress()
    {
        if (IsHost && !string.IsNullOrEmpty(HostIp)) return HostIp;
        if (!string.IsNullOrEmpty(HostIp))
        {
            var local = HotspotService.GetLocalIpToward(HostIp);   // adapter on the host's network
            if (local != null) return local;
        }
        return HotspotService.GetBestLocalIp();
    }

    public async Task<FileTransfer?> OfferFileAsync(Member target, string filePath)
    {
        if (!IsConnected) return null;
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
                senderIp = GetMyAddress()
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
                if (!IsViewingChat(null))
                {
                    AddUnread(null);
                    NotificationService.Instance.Show(msg.SenderName, msg.Payload, NotificationKind.Info);
                }
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
                GetThread(msg.SenderId).Add(dm);
                Db.SaveChatMessage(dm);
                if (!IsViewingChat(msg.SenderId))
                {
                    AddUnread(msg.SenderId);
                    NotificationService.Instance.Show($"DM from {msg.SenderName}", msg.Payload, NotificationKind.Info);
                }
                break;

            case MessageType.CodeShare:
                var codeData = JsonConvert.DeserializeObject<CodeShareData>(msg.Payload);
                if (codeData != null)
                {
                    ReceivedSnippets.Insert(0, new ReceivedSnippet
                    {
                        SenderName = msg.SenderName,
                        Language   = codeData.Language,
                        Code       = codeData.Code,
                        Received   = DateTime.Now
                    });
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
                    FileOfferReceived?.Invoke(incoming, offer.SenderIp, offer.TcpPort, msg.SenderId);
                }
                break;

            case MessageType.FileDecline:
                var declined = Transfers.FirstOrDefault(t => t.Id == msg.Payload && t.Direction == TransferDirection.Sending);
                if (declined != null)
                {
                    declined.Status = TransferStatus.Declined;
                    _fileTransfer.CancelSend(declined.Id);
                    Db.SaveTransfer(declined);
                    NotificationService.Instance.Show("File declined",
                        $"{msg.SenderName} declined {declined.FileName}", NotificationKind.Warning);
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
    public event Action<FileTransfer, string, int, string>? FileOfferReceived; // transfer, senderIp, tcpPort, senderId

    // ── File Accept / Decline ─────────────────────────────────────────────────

    public async Task AcceptFileAsync(FileTransfer transfer, string senderIp, int tcpPort)
    {
        var savePath = GetUniqueSavePath(transfer.FileName);
        transfer.SavePath = savePath;

        await _fileTransfer.ReceiveFileAsync(transfer, senderIp, tcpPort, savePath);
        Db.SaveTransfer(transfer);
    }

    public async Task DeclineFileAsync(FileTransfer transfer)
    {
        transfer.Status = TransferStatus.Declined;
        Db.SaveTransfer(transfer);
        // Tell the sender so it stops waiting
        await SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.FileDecline,
            SenderName = _settings.DisplayName,
            RecipientId = transfer.PeerId,
            Payload = transfer.Id
        });
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

        if (IsHost)
        {
            var me = Members.FirstOrDefault(m => m.Id == "host");
            if (me != null) me.DisplayName = newName;
            if (_server?.HostMember != null) _server.HostMember.DisplayName = newName;
        }

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
            msg.SenderName = _settings.DisplayName;
            await _server.SendFromHostAsync(msg);   // routes to one recipient, or everyone
        }
        else if (_client != null)
            await _client.SendAsync(msg);
    }

    /// <summary>Returns the DM thread for a peer, loading its saved history the first time.</summary>
    public ObservableCollection<ChatMessage> GetThread(string peerId)
    {
        if (!DirectThreads.TryGetValue(peerId, out var thread))
            DirectThreads[peerId] = thread = new ObservableCollection<ChatMessage>(Db.LoadDirectHistory(peerId));
        return thread;
    }

    private bool IsViewingChat(string? peerId) =>
        CurrentPage == AppPage.Chat && ActiveChatPeer == peerId && Application.Current.MainWindow?.IsActive == true;

    private void AddUnread(string? peerId)
    {
        var key = peerId ?? "";
        _unread[key] = UnreadFor(peerId) + 1;
        OnPropertyChanged(nameof(TotalUnread));
        UnreadChanged?.Invoke();
    }

    public void MarkRead(string? peerId)
    {
        if (_unread.Remove(peerId ?? ""))
        {
            OnPropertyChanged(nameof(TotalUnread));
            UnreadChanged?.Invoke();
        }
    }

    public void ClearChatHistory(string? peerId)
    {
        Db.ClearChat(peerId);
        if (peerId == null) GroupMessages.Clear();
        else GetThread(peerId).Clear();
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

public class ReceivedSnippet
{
    public string SenderName { get; set; } = "";
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
    public DateTime Received { get; set; }
    public string Header => $"{SenderName} · {Language} · {Received:HH:mm}";
    public string Preview => Code.Length <= 120 ? Code : Code[..120] + "…";
}

public class ActivityItem
{
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }
    public string TimeText => Time.ToString("HH:mm");
}
