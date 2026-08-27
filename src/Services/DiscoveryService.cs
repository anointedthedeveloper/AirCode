using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirCode.Services;

/// <summary>
/// UDP-based LAN discovery.
/// Host listens and responds; clients broadcast a probe to find the host.
/// </summary>
public class DiscoveryService : IDisposable
{
    private const int DiscoveryPort = 45678;
    private const string DiscoveryMagic = "AIRCODE_DISCOVER_V1";
    private const string DiscoveryReply = "AIRCODE_HOST_V1";

    private CancellationTokenSource? _cts;
    private bool _disposed;

    // ── Host side ─────────────────────────────────────────────────────────────

    public void StartHostBeacon(int wsPort)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient(DiscoveryPort);
                udp.EnableBroadcast = true;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udp.ReceiveAsync(token);
                        var msg = Encoding.UTF8.GetString(result.Buffer);
                        if (msg == DiscoveryMagic)
                        {
                            var reply = Encoding.UTF8.GetBytes($"{DiscoveryReply}:{wsPort}");
                            await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* ignore transient errors */ }
                }
            }
            catch { }
        }, token);
    }

    // ── Client side ───────────────────────────────────────────────────────────

    /// <summary>Broadcasts a discovery probe; returns host (ip, port) or null on timeout.</summary>
    public async Task<(string ip, int port)?> DiscoverHostAsync(TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var probe = Encoding.UTF8.GetBytes(DiscoveryMagic);
            var ep = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            await udp.SendAsync(probe, probe.Length, ep);

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                var msg = Encoding.UTF8.GetString(result.Buffer);
                if (msg.StartsWith(DiscoveryReply + ":"))
                {
                    var parts = msg.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                        return (result.RemoteEndPoint.Address.ToString(), port);
                }
            }
            catch (OperationCanceledException) { }
        }
        catch { }
        return null;
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
    }
}
