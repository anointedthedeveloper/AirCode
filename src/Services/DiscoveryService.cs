using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirCode.Services;

/// <summary>
/// UDP LAN discovery. Host listens on port 45678 and replies with its WS port.
/// Client broadcasts a probe and waits for a reply.
/// Sends on ALL network interfaces so subnets are covered.
/// </summary>
public class DiscoveryService : IDisposable
{
    private const int    Port          = 45678;
    private const string Magic         = "AIRCODE_DISCOVER_V1";
    private const string ReplyPrefix   = "AIRCODE_HOST_V1:";

    private CancellationTokenSource? _cts;
    private bool _disposed;

    // ── Host beacon ───────────────────────────────────────────────────────────

    public void StartHostBeacon(int wsPort)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var replyPayload = Encoding.UTF8.GetBytes($"{ReplyPrefix}{wsPort}");

        Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                udp.EnableBroadcast = true;

                LogService.Instance.Info("Discovery", $"Host beacon listening on UDP {Port}");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udp.ReceiveAsync(token);
                        var txt = Encoding.UTF8.GetString(res.Buffer);
                        if (txt.Trim() == Magic)
                        {
                            LogService.Instance.Debug("Discovery",
                                $"Probe from {res.RemoteEndPoint} — replying");
                            await udp.SendAsync(replyPayload, replyPayload.Length,
                                res.RemoteEndPoint);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* transient */ }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error("Discovery", $"Beacon error: {ex.Message}");
            }
        }, token);
    }

    // ── Client probe ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a UDP broadcast probe and waits up to <paramref name="timeout"/> for a reply.
    /// Returns (hostIp, wsPort) or null.
    /// </summary>
    public async Task<(string ip, int port)?> DiscoverHostAsync(TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress, true);
            // Bind to any port so we receive the reply
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var probe = Encoding.UTF8.GetBytes(Magic);

            // Broadcast on all interfaces
            await udp.SendAsync(probe, probe.Length,
                new IPEndPoint(IPAddress.Broadcast, Port));

            // Also try subnet-directed broadcasts for common subnets
            foreach (var subnet in GetCommonBroadcasts())
            {
                try { await udp.SendAsync(probe, probe.Length,
                    new IPEndPoint(subnet, Port)); }
                catch { }
            }

            LogService.Instance.Debug("Discovery", "Probe sent — waiting for reply…");

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var res = await udp.ReceiveAsync(cts.Token);
                    var txt = Encoding.UTF8.GetString(res.Buffer).Trim();
                    if (txt.StartsWith(ReplyPrefix))
                    {
                        var portStr = txt[ReplyPrefix.Length..];
                        if (int.TryParse(portStr, out var wsPort))
                        {
                            var hostIp = res.RemoteEndPoint.Address.ToString();
                            LogService.Instance.Success("Discovery",
                                $"Host found: {hostIp}:{wsPort}");
                            return (hostIp, wsPort);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("Discovery", $"Probe error: {ex.Message}");
        }
        return null;
    }

    private static IEnumerable<IPAddress> GetCommonBroadcasts()
    {
        // Common phone-hotspot and router subnets
        yield return IPAddress.Parse("192.168.1.255");
        yield return IPAddress.Parse("192.168.0.255");
        yield return IPAddress.Parse("192.168.43.255");  // Android hotspot
        yield return IPAddress.Parse("192.168.137.255"); // Windows hotspot
        yield return IPAddress.Parse("10.0.0.255");
        yield return IPAddress.Parse("172.20.10.255");   // iPhone hotspot
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
    }
}
