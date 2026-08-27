using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using AirCode.Models;
using Newtonsoft.Json;

namespace AirCode.Services;

/// <summary>
/// Lightweight WebSocket server for the AirCode host.
/// Listens on all interfaces so hotspot clients (192.168.137.x) and
/// localhost connections both reach the same server instance.
/// </summary>
public class WebSocketServer : IDisposable
{
    private const int MaxMessageSize = 256 * 1024;
    public const int WsPort = 45679;

    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private bool _disposed;

    public event Action<Member>?  ClientJoined;
    public event Action<string>?  ClientLeft;
    public event Action<NetworkMessage>? MessageReceived;
    public event Action<Member>?  ClientUpdated;

    public IEnumerable<Member> ConnectedMembers => _clients.Values.Select(c => c.Member);

    public async Task StartAsync(string _ip)
    {
        _listener = new HttpListener();
        // Bind to all interfaces — required for hotspot clients on a different subnet
        _listener.Prefixes.Add($"http://+:{WsPort}/ws/");
        try
        {
            _listener.Start();
        }
        catch
        {
            // Fallback: bind only to localhost (no admin rights for +)
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{WsPort}/ws/");
            _listener.Prefixes.Add($"http://127.0.0.1:{WsPort}/ws/");
            _listener.Start();
        }

        _ = Task.Run(AcceptLoop, _cts.Token);
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
            catch { /* swallow transient errors */ }
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        var wsCtx  = await ctx.AcceptWebSocketAsync(null);
        var ws     = wsCtx.WebSocket;
        var clientId = Guid.NewGuid().ToString();

        try
        {
            // First message must be Register
            var regMsg = await ReceiveMessageAsync(ws, _cts.Token);
            if (regMsg == null || regMsg.Type != MessageType.Register)
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "Register first", CancellationToken.None);
                return;
            }

            var member = new Member
            {
                Id = clientId,
                DisplayName = regMsg.SenderName,
                DeviceName  = regMsg.Payload
            };

            var client = new ConnectedClient(clientId, ws, member);
            _clients[clientId] = client;

            // ACK with assigned id
            await SendToClientAsync(client, new NetworkMessage
            {
                Type       = MessageType.RegisterAck,
                SenderId   = "host",
                SenderName = "AirCode Host",
                RecipientId = clientId,
                Payload    = clientId
            });

            // Send current member list to new client
            var memberList = _clients.Values.Select(c => new
            {
                c.Member.Id, c.Member.DisplayName,
                c.Member.DeviceName, c.Member.IsHost
            });
            await SendToClientAsync(client, new NetworkMessage
            {
                Type     = MessageType.MemberList,
                SenderId = "host",
                Payload  = JsonConvert.SerializeObject(memberList)
            });

            // Notify everyone else of the joiner
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

            // Message pump
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var msg = await ReceiveMessageAsync(ws, _cts.Token);
                if (msg == null) break;

                msg.SenderId   = clientId;
                msg.SenderName = member.DisplayName;

                await RouteMessageAsync(msg);
                MessageReceived?.Invoke(msg);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(clientId, out _);
            await BroadcastAsync(new NetworkMessage
            {
                Type     = MessageType.MemberLeft,
                SenderId = clientId,
                Payload  = clientId
            });
            ClientLeft?.Invoke(clientId);
            try { ws.Dispose(); } catch { }
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

    public async Task BroadcastAsync(NetworkMessage msg, string? exclude = null)
    {
        var json  = JsonConvert.SerializeObject(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        var tasks = _clients.Values
            .Where(c => c.Id != exclude && c.WebSocket.State == WebSocketState.Open)
            .Select(c => SafeSendAsync(c.WebSocket, bytes));
        await Task.WhenAll(tasks);
    }

    public async Task SendToClientAsync(ConnectedClient client, NetworkMessage msg)
    {
        var json  = JsonConvert.SerializeObject(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SafeSendAsync(client.WebSocket, bytes);
    }

    private static async Task SafeSendAsync(WebSocket ws, byte[] bytes)
    {
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }

    private static async Task<NetworkMessage?> ReceiveMessageAsync(
        WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageSize];
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

public class ConnectedClient
{
    public string    Id        { get; }
    public WebSocket WebSocket { get; }
    public Member    Member    { get; }
    public ConnectedClient(string id, WebSocket ws, Member member)
    { Id = id; WebSocket = ws; Member = member; }
}
