using System.Net.WebSockets;
using System.Text;
using AirCode.Models;
using Newtonsoft.Json;

namespace AirCode.Services;

/// <summary>WebSocket client that connects to the AirCode host.</summary>
public class WebSocketClient : IDisposable
{
    private const int  PingSeconds    = 10;
    private const int  DeadAfterSecs  = 30;   // no traffic (not even a Pong) for this long = link is dead

    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private DateTime _lastRx = DateTime.UtcNow;
    private int _disconnectedRaised;
    private bool _disposed;

    public string? MyId { get; private set; }
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<NetworkMessage>? MessageReceived;

    public async Task<bool> ConnectAsync(string ip, int port,
        string displayName, string deviceName, string uid, int timeoutSecs = 8)
    {
        var log = LogService.Instance;
        log.Info("Client", $"Connecting to ws://{ip}:{port}/");
        try
        {
            _ws  = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            _lastRx = DateTime.UtcNow;
            _disconnectedRaised = 0;

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));
            await _ws.ConnectAsync(new Uri($"ws://{ip}:{port}/"), cts2.Token);

            log.Success("Client", $"WebSocket connected to {ip}:{port}");

            await SendAsync(new NetworkMessage
            {
                Type       = MessageType.Register,
                SenderName = displayName,
                Payload    = JsonConvert.SerializeObject(new RegisterData(deviceName, uid))
            });

            var token = _cts.Token;
            _ = Task.Run(() => ReceiveLoop(token));
            _ = Task.Run(() => HeartbeatLoop(token));
            Connected?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Client", $"Connect failed: {ex.Message}");
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            return false;
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        var buffer = new byte[256 * 1024];
        var sb = new StringBuilder();

        while (_ws?.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            try
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    { RaiseDisconnected(); return; }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                _lastRx = DateTime.UtcNow;

                var msg = JsonConvert.DeserializeObject<NetworkMessage>(sb.ToString());
                if (msg == null || msg.Type == MessageType.Pong) continue;

                if (msg.Type == MessageType.RegisterAck)
                    MyId = msg.Payload;

                MessageReceived?.Invoke(msg);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch { }
        }

        RaiseDisconnected();
    }

    /// <summary>Pings the host and declares the link dead if nothing comes back (host left, out of range…).</summary>
    private async Task HeartbeatLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(PingSeconds), token); }
            catch (OperationCanceledException) { return; }

            if ((DateTime.UtcNow - _lastRx).TotalSeconds > DeadAfterSecs)
            {
                LogService.Instance.Warn("Client", "Host stopped responding — connection lost.");
                try { _ws?.Abort(); } catch { }
                return; // ReceiveLoop notices the abort and raises Disconnected
            }
            await SendAsync(new NetworkMessage { Type = MessageType.Ping });
        }
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
            Disconnected?.Invoke();
    }

    public async Task SendAsync(NetworkMessage msg)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        try
        {
            msg.SenderId = MyId ?? "";
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg));
            await _sendLock.WaitAsync();
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { _sendLock.Release(); }
        }
        catch { }
    }

    /// <summary>Closes the connection without raising <see cref="Disconnected"/>.</summary>
    public async Task DisconnectAsync()
    {
        Interlocked.Exchange(ref _disconnectedRaised, 1); // deliberate: don't trigger auto-reconnect
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", t.Token);
            }
        }
        catch { }
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _disconnectedRaised, 1);
        _cts.Cancel();
        _ws?.Dispose();
    }
}
