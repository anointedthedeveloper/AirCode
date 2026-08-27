using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Diagnostics;

namespace AirCode.Services;

// Kept for compatibility but simplified — AirCode no longer creates hotspots.
// The user creates their own Wi-Fi (phone hotspot, Windows hotspot, router).
// AirCode just finds its IP on that network and starts the server.
public enum HotspotResult  { Success, NotSupported, Failed, NoAdapter }
public enum HotspotMethod  { None }

public class HotspotService
{
    public bool IsHotspotActive => false; // AirCode never creates a hotspot now
    public HotspotMethod ActiveMethod => HotspotMethod.None;

    public Task StopAsync() => Task.CompletedTask;

    // ── Network adapter helpers ───────────────────────────────────────────────

    /// <summary>
    /// Returns the best IP for this machine on the current network.
    /// Prefers Wi-Fi, then Ethernet. Skips virtual/loopback adapters.
    /// This is the IP clients will connect to — whatever network the host is on.
    /// </summary>
    public static string GetBestLocalIp()
    {
        var log = LogService.Instance;

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

    public static void RestartAsAdmin()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "AirCode.exe";
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); }
        catch { return; }
        System.Windows.Application.Current.Shutdown();
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
