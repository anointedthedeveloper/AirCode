using System.Net.WebSockets;
using System.Text;
using AirCode.Models;
using Newtonsoft.Json;

namespace AirCode.Services;

/// <summary>WebSocket client that connects to the AirCode host.</summary>
public class WebSocketClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public string? MyId { get; private set; }
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<NetworkMessage>? MessageReceived;

    public async Task<bool> ConnectAsync(string ip, int port,
        string displayName, string deviceName)
    {
        try
        {
            _ws  = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            // Try /ws/ path first (matches server prefix)
            Uri uri;
            try
            {
                uri = new Uri($"ws://{ip}:{port}/ws/");
                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ws.ConnectAsync(uri, cts2.Token);
            }
            catch
            {
                // Retry without path
                _ws  = new ClientWebSocket();
                uri  = new Uri($"ws://{ip}:{port}/");
                using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ws.ConnectAsync(uri, cts3.Token);
            }

            await SendAsync(new NetworkMessage
            {
                Type       = MessageType.Register,
                SenderName = displayName,
                Payload    = deviceName
            });

            _ = Task.Run(ReceiveLoop, _cts.Token);
            Connected?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[256 * 1024];

        while (_ws?.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            try
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    { Disconnected?.Invoke(); return; }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var msg = JsonConvert.DeserializeObject<NetworkMessage>(sb.ToString());
                if (msg == null) continue;

                if (msg.Type == MessageType.RegisterAck)
                    MyId = msg.Payload;

                MessageReceived?.Invoke(msg);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch { }
        }

        Disconnected?.Invoke();
    }

    public async Task SendAsync(NetworkMessage msg)
    {
        if (_ws?.State != WebSocketState.Open) return;
        try
        {
            msg.SenderId = MyId ?? "";
            var json  = JsonConvert.SerializeObject(msg);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Bye", CancellationToken.None);
        }
        catch { }
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _ws?.Dispose();
    }
}
