using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using AirCode.Models;
using Newtonsoft.Json;

namespace AirCode.Services;

public class WebSocketServer : IDisposable
{
    private const int MaxMessageSize = 256 * 1024;
    public const int WsPort = 45679;

    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private bool _disposed;

    /// <summary>Seconds without any traffic (clients ping every 10 s) before a client is dropped.</summary>
    private const int IdleTimeoutSeconds = 35;

    /// <summary>The host's own entry, included in member lists so clients can see/DM the host.</summary>
    public Member? HostMember { get; set; }

    public event Action<Member>?         ClientJoined;
    public event Action<string>?         ClientLeft;
    public event Action<NetworkMessage>? MessageReceived;
    public event Action<Member>?         ClientUpdated;

    public IEnumerable<Member> ConnectedMembers => _clients.Values.Select(c => c.Member);

    public async Task StartAsync(string _ip)
    {
        var log = LogService.Instance;
        _listener = new HttpListener();

        // Bind to all interfaces — works when running as Administrator
        // or when URL ACL has been reserved.
        try
        {
            _listener.Prefixes.Add($"http://+:{WsPort}/");
            _listener.Start();
            log.Success("Server", $"Listening on all interfaces http://+:{WsPort}/");
        }
        catch
        {
            // Fallback: bind to localhost only (non-admin, no ACL)
            _listener.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{WsPort}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{WsPort}/");
            try
            {
                _listener.Start();
                log.Warn("Server",
                    $"Bound to localhost:{WsPort} only — clients on other PCs cannot connect. " +
                    "Run as Administrator for full network access.");
            }
            catch (Exception ex)
            {
                log.Error("Server", $"Could not start listener: {ex.Message}");
                throw;
            }
        }

        _ = Task.Run(AcceptLoop, _cts.Token);
        _ = Task.Run(IdleSweepLoop, _cts.Token);
        await Task.CompletedTask;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync();
                if (ctx.Request.IsWebSocketRequest)
                    _ = Task.Run(() => HandleClientAsync(ctx));
                else
                {
                    ctx.Response.StatusCode = 426;
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket; }
        catch { try { ctx.Response.Abort(); } catch { } return; }

        var buffer   = new byte[MaxMessageSize];
        var clientId = Guid.NewGuid().ToString();
        ConnectedClient? client = null;
        var registered = false;

        try
        {
            var regMsg = await ReceiveMessageAsync(ws, buffer, _cts.Token);
            if (regMsg == null || regMsg.Type != MessageType.Register)
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "Register first", CancellationToken.None);
                return;
            }

            // Register payload: {"device": "...", "uid": "..."}. A stable uid lets a client keep
            // its identity (and therefore its DM history) across reconnects.
            string device = regMsg.Payload, uid = "";
            try
            {
                var reg = JsonConvert.DeserializeObject<RegisterData>(regMsg.Payload);
                if (reg != null) { device = reg.Device; uid = reg.Uid; }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(uid) && uid != "host") clientId = uid;

            var member = new Member
            {
                Id          = clientId,
                DisplayName = string.IsNullOrWhiteSpace(regMsg.SenderName) ? "Guest" : regMsg.SenderName,
                DeviceName  = device
            };

            client = new ConnectedClient(clientId, ws, member);

            // Same identity reconnecting before the old socket timed out: replace it.
            if (_clients.TryGetValue(clientId, out var stale))
            {
                _clients.TryRemove(clientId, out _);
                try { stale.WebSocket.Abort(); } catch { }
                ClientLeft?.Invoke(clientId);
            }
            _clients[clientId] = client;
            registered = true;

            LogService.Instance.Info("Server", $"Client registered: {member.DisplayName} ({clientId})");

            await SendToClientAsync(client, new NetworkMessage
            {
                Type        = MessageType.RegisterAck,
                SenderId    = "host",
                SenderName  = "AirCode Host",
                RecipientId = clientId,
                Payload     = clientId
            });

            await SendToClientAsync(client, new NetworkMessage
            {
                Type     = MessageType.MemberList,
                SenderId = "host",
                Payload  = JsonConvert.SerializeObject(SnapshotMembers())
            });

            await BroadcastAsync(new NetworkMessage
            {
                Type       = MessageType.MemberJoined,
                SenderId   = clientId,
                SenderName = member.DisplayName,
                Payload    = JsonConvert.SerializeObject(new
                {
                    member.Id, member.DisplayName,
                    member.DeviceName, member.IsHost
                })
            }, exclude: clientId);

            ClientJoined?.Invoke(member);

            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var msg = await ReceiveMessageAsync(ws, buffer, _cts.Token);
                if (msg == null) break;
                client.LastSeen = DateTime.UtcNow;
                msg.SenderId   = clientId;
                msg.SenderName = member.DisplayName;
                await RouteMessageAsync(msg);
                // The host only consumes what is addressed to it (or to everyone)
                if (msg.Type != MessageType.Ping &&
                    (string.IsNullOrEmpty(msg.RecipientId) || msg.RecipientId == "host"))
                    MessageReceived?.Invoke(msg);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Instance.Warn("Server", $"Client handler error: {ex.Message}");
        }
        finally
        {
            // Only remove/announce if this socket is still the registered one for the id
            if (registered && client != null &&
                ((ICollection<KeyValuePair<string, ConnectedClient>>)_clients)
                    .Remove(new KeyValuePair<string, ConnectedClient>(clientId, client)))
            {
                await BroadcastAsync(new NetworkMessage
                {
                    Type     = MessageType.MemberLeft,
                    SenderId = clientId,
                    Payload  = clientId
                });
                ClientLeft?.Invoke(clientId);
            }
            try { ws.Dispose(); } catch { }
        }
    }

    private List<object> SnapshotMembers()
    {
        var list = new List<object>();
        if (HostMember != null)
            list.Add(new { HostMember.Id, HostMember.DisplayName, HostMember.DeviceName, IsHost = true });
        foreach (var c in _clients.Values)
            list.Add(new { c.Member.Id, c.Member.DisplayName, c.Member.DeviceName, c.Member.IsHost });
        return list;
    }

    /// <summary>Drops clients that stopped talking (e.g. walked out of hotspot range).</summary>
    private async Task IdleSweepLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token); }
            catch (OperationCanceledException) { return; }

            var cutoff = DateTime.UtcNow.AddSeconds(-IdleTimeoutSeconds);
            foreach (var c in _clients.Values.Where(c => c.LastSeen < cutoff).ToList())
            {
                LogService.Instance.Warn("Server", $"{c.Member.DisplayName} timed out — dropping");
                try { c.WebSocket.Abort(); } catch { }
            }
        }
    }

    private async Task RouteMessageAsync(NetworkMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.NameChange:
                if (_clients.TryGetValue(msg.SenderId, out var c))
                {
                    c.Member.DisplayName = msg.Payload;
                    ClientUpdated?.Invoke(c.Member);
                }
                await BroadcastAsync(msg);
                break;

            case MessageType.Ping:
                if (_clients.TryGetValue(msg.SenderId, out var pc))
                    await SendToClientAsync(pc, new NetworkMessage
                    { Type = MessageType.Pong, SenderId = "host" });
                break;

            default:
                if (msg.RecipientId == "host")
                    break; // addressed to the host — consumed by the caller
                if (!string.IsNullOrEmpty(msg.RecipientId))
                {
                    if (_clients.TryGetValue(msg.RecipientId, out var target))
                        await SendToClientAsync(target, msg);
                }
                else
                    await BroadcastAsync(msg, exclude: msg.SenderId);
                break;
        }
    }

    /// <summary>Delivers a host-originated message to its recipient (or everyone if none).</summary>
    public async Task SendFromHostAsync(NetworkMessage msg)
    {
        msg.SenderId = "host";
        if (string.IsNullOrEmpty(msg.RecipientId))
            await BroadcastAsync(msg);
        else if (_clients.TryGetValue(msg.RecipientId, out var target))
            await SendToClientAsync(target, msg);
    }

    public async Task BroadcastAsync(NetworkMessage msg, string? exclude = null)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg));
        var tasks = _clients.Values
            .Where(c => c.Id != exclude && c.WebSocket.State == WebSocketState.Open)
            .Select(c => SafeSendAsync(c, bytes));
        await Task.WhenAll(tasks);
    }

    public async Task SendToClientAsync(ConnectedClient client, NetworkMessage msg)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg));
        await SafeSendAsync(client, bytes);
    }

    // A WebSocket allows only one outstanding SendAsync — serialise per client.
    private static async Task SafeSendAsync(ConnectedClient client, byte[] bytes)
    {
        try
        {
            await client.SendLock.WaitAsync();
            try
            {
                if (client.WebSocket.State != WebSocketState.Open) return;
                await client.WebSocket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { client.SendLock.Release(); }
        }
        catch { }
    }

    private static async Task<NetworkMessage?> ReceiveMessageAsync(
        WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            try { result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch { return null; }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);

        try { return JsonConvert.DeserializeObject<NetworkMessage>(sb.ToString()); }
        catch { return null; }
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _listener?.Close(); } catch { }
    }
}

public record RegisterData(string Device, string Uid);

public class ConnectedClient
{
    public string    Id        { get; }
    public WebSocket WebSocket { get; }
    public Member    Member    { get; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public DateTime  LastSeen  { get; set; } = DateTime.UtcNow;
    public ConnectedClient(string id, WebSocket ws, Member member)
    { Id = id; WebSocket = ws; Member = member; }
}
