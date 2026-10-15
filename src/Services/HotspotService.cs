using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Diagnostics;

namespace AirCode.Services;

/// <summary>
/// Network helper for the host PC.
///
/// Topology: the host PC joins a Wi-Fi network (the uplink) and then shares it as a
/// Windows Mobile Hotspot. Every other device joins that hotspot and launches AirCode.
/// The hotspot gives the host a fixed address (192.168.137.1 by default) which clients
/// find via UDP discovery or by probing their default gateway.
/// </summary>
public enum HotspotResult  { Success, AlreadyOn, NoConnection, Failed }

public sealed record HotspotInfo(HotspotResult Result, string Ssid, string Passphrase, string Ip, string Message);

public class HotspotService
{
    private Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager? _manager;
    private bool _startedByUs;

    public bool IsHotspotActive { get; private set; }
    public string Ssid { get; private set; } = "";
    public string Passphrase { get; private set; } = "";

    /// <summary>
    /// Turns on the Windows Mobile Hotspot, sharing the Wi-Fi (or Ethernet) connection this PC
    /// is currently using. Falls back gracefully — the caller can still host on the existing network.
    /// </summary>
    public async Task<HotspotInfo> StartAsync(string ssid, string passphrase)
    {
        var log = LogService.Instance;
        try
        {
            var profiles = new List<Windows.Networking.Connectivity.ConnectionProfile>();
            var internet = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (internet != null) profiles.Add(internet);
            foreach (var p in Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
                if (p.GetNetworkConnectivityLevel() != Windows.Networking.Connectivity.NetworkConnectivityLevel.None
                    && !profiles.Any(x => x.ProfileName == p.ProfileName))
                    profiles.Add(p);

            if (profiles.Count == 0)
            {
                log.Warn("Hotspot", "No active connection to share. Connect this PC to Wi-Fi first.");
                return new HotspotInfo(HotspotResult.NoConnection, "", "", "",
                    "This PC is not connected to a Wi-Fi network, so there is nothing to share. Connect to Wi-Fi first.");
            }

            string lastError = "";
            foreach (var profile in profiles)
            {
                try
                {
                    var mgr = Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager
                        .CreateFromConnectionProfile(profile);

                    // Hotspot already running (e.g. user enabled it in Settings) — reuse it as is.
                    if (mgr.TetheringOperationalState == Windows.Networking.NetworkOperators.TetheringOperationalState.On)
                    {
                        var cur = mgr.GetCurrentAccessPointConfiguration();
                        _manager = mgr; _startedByUs = false;
                        IsHotspotActive = true;
                        Ssid = cur.Ssid; Passphrase = cur.Passphrase;
                        var ipOn = await WaitForHotspotIpAsync();
                        log.Info("Hotspot", $"Windows hotspot already on: {Ssid} @ {ipOn}");
                        return new HotspotInfo(HotspotResult.AlreadyOn, Ssid, Passphrase, ipOn,
                            "Windows hotspot was already on — AirCode is using it.");
                    }

                    var cfg = new Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
                    {
                        Ssid = ssid,
                        Passphrase = passphrase
                    };
                    // 2.4 GHz works with every phone/laptop; ignore if the adapter can't choose.
                    try
                    {
                        if (cfg.IsBandSupported(Windows.Networking.NetworkOperators.TetheringWiFiBand.TwoPointFourGigahertz))
                            cfg.Band = Windows.Networking.NetworkOperators.TetheringWiFiBand.TwoPointFourGigahertz;
                    }
                    catch { }

                    await mgr.ConfigureAccessPointAsync(cfg);
                    var result = await mgr.StartTetheringAsync();
                    if (result.Status == Windows.Networking.NetworkOperators.TetheringOperationStatus.Success)
                    {
                        _manager = mgr; _startedByUs = true;
                        IsHotspotActive = true;
                        Ssid = ssid; Passphrase = passphrase;
                        var ip = await WaitForHotspotIpAsync();
                        log.Success("Hotspot", $"Hotspot '{ssid}' started @ {ip}");
                        return new HotspotInfo(HotspotResult.Success, ssid, passphrase, ip, "Hotspot started.");
                    }
                    lastError = string.IsNullOrEmpty(result.AdditionalErrorMessage)
                        ? result.Status.ToString() : $"{result.Status}: {result.AdditionalErrorMessage}";
                    log.Warn("Hotspot", $"StartTethering failed on '{profile.ProfileName}': {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    log.Warn("Hotspot", $"Hotspot attempt on '{profile.ProfileName}' failed: {ex.Message}");
                }
            }

            return new HotspotInfo(HotspotResult.Failed, "", "", "",
                $"Windows could not start the hotspot ({lastError}). " +
                "Make sure Wi-Fi is on and your adapter supports Mobile Hotspot.");
        }
        catch (Exception ex)
        {
            log.Error("Hotspot", $"Hotspot error: {ex.Message}");
            return new HotspotInfo(HotspotResult.Failed, "", "", "", ex.Message);
        }
    }

    public async Task StopAsync()
    {
        var mgr = _manager;
        var ours = _startedByUs;
        _manager = null; _startedByUs = false; IsHotspotActive = false;
        if (mgr == null || !ours) return;
        try
        {
            await mgr.StopTetheringAsync();
            LogService.Instance.Info("Hotspot", "Hotspot stopped.");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("Hotspot", $"Could not stop hotspot: {ex.Message}");
        }
    }

    /// <summary>The hotspot adapter takes a moment to get its address — poll for it.</summary>
    private static async Task<string> WaitForHotspotIpAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            var ip = FindHotspotIp();
            if (ip != null) return ip;
            await Task.Delay(500);
        }
        return "192.168.137.1"; // Windows' default for the shared-connection adapter
    }

    /// <summary>Finds the address of the hotspot (ICS / Wi-Fi Direct virtual) adapter.</summary>
    public static string? FindHotspotIp()
    {
        string? fallback = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var ip = FirstIPv4(ni);
            if (ip == null) continue;
            if (ip.StartsWith("192.168.137.")) return ip;
            if (fallback == null && IsVirtualAdapter(ni.Description) &&
                !ni.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                fallback = ip;
        }
        return fallback;
    }

    /// <summary>The local address the OS would use to reach <paramref name="remoteIp"/>.</summary>
    public static string? GetLocalIpToward(string remoteIp)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Parse(remoteIp), 9); // UDP connect sends nothing; it only picks a route
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    /// <summary>Default gateways of all active adapters — on a hotspot the gateway IS the host.</summary>
    public static List<string> GetGatewayAddresses()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var g in ni.GetIPProperties().GatewayAddresses)
                if (g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any))
                    result.Add(g.Address.ToString());
        }
        return result.Distinct().ToList();
    }

    /// <summary>Directed-broadcast addresses (e.g. 192.168.137.255) of every active adapter.</summary>
    public static List<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new IPAddress(bc));
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Adds an inbound Windows Firewall rule for AirCode so other devices can reach it
    /// (chat server, UDP discovery, file transfers). Best-effort; needs admin.
    /// </summary>
    public static async Task EnsureFirewallRuleAsync()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            await RunNetshAsync("advfirewall firewall delete rule name=\"AirCode\"");
            await RunNetshAsync(
                $"advfirewall firewall add rule name=\"AirCode\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");
            LogService.Instance.Debug("Network", "Firewall rule ensured.");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("Network", $"Firewall rule failed (non-fatal): {ex.Message}");
        }
    }

    public static async Task RunNetshAsync(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    // ── Network adapter helpers ───────────────────────────────────────────────

    /// <summary>
    /// Returns the best IP for this machine on the current network.
    /// Prefers Wi-Fi, then Ethernet. Skips virtual/loopback adapters.
    /// This is the IP clients will connect to — whatever network the host is on.
    /// </summary>
    public static string GetBestLocalIp()
    {
        var log = LogService.Instance;

        // 0. A running Windows hotspot always wins — that is where the clients are.
        var hs = FindHotspotIp();
        if (hs != null && hs.StartsWith("192.168.137."))
        {
            log.Info("Network", $"Using hotspot IP: {hs}");
            return hs;
        }

        // 1. Physical Wi-Fi (skip virtual/Direct adapters)
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            if (IsVirtualAdapter(ni.Description)) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
            {
                log.Info("Network", $"Using Wi-Fi IP: {ip}  ({ni.Description})");
                return ip;
            }
        }

        // 2. Ethernet
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
            {
                log.Info("Network", $"Using Ethernet IP: {ip}  ({ni.Description})");
                return ip;
            }
        }

        // 3. Any other non-loopback adapter
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (IsVirtualAdapter(ni.Description)) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
            {
                log.Info("Network", $"Using adapter IP: {ip}  ({ni.Description})");
                return ip;
            }
        }

        log.Warn("Network", "No usable network adapter found. Connect to a Wi-Fi network first.");
        return "";
    }

    /// <summary>Returns all active non-virtual adapters with their IPs.</summary>
    public static List<(string name, string ip, NetworkInterfaceType type)> GetAllAdapters()
    {
        var result = new List<(string, string, NetworkInterfaceType)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (IsVirtualAdapter(ni.Description)) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
                result.Add((ni.Description, ip, ni.NetworkInterfaceType));
        }
        return result;
    }

    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsVirtualAdapter(string desc) =>
        desc.Contains("Wi-Fi Direct",      StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Virtual WiFi",      StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Microsoft Virtual", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Loopback",          StringComparison.OrdinalIgnoreCase);

    private static string? FirstIPv4(NetworkInterface ni)
    {
        foreach (var a in ni.GetIPProperties().UnicastAddresses)
            if (a.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a.Address))
                return a.Address.ToString();
        return null;
    }
}
