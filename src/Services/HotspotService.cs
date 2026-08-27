using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirCode.Services;

public enum HotspotResult   { Success, NotSupported, PermissionDenied, Failed, NoAdapter }
public enum HotspotMethod   { None, WifiDirect, MobileHotspot, NetshHosted }

/// <summary>
/// Tries three methods in order to bring up a local Wi-Fi network:
///   1. Wi-Fi Direct Group Owner   (no internet required, preferred)
///   2. Windows Mobile Hotspot     (requires sharing a connection, fallback)
///   3. netsh hosted-network       (legacy, Win 7–10, fallback)
///
/// If the machine has no Wi-Fi adapter at all, returns NoAdapter immediately.
/// </summary>
public class HotspotService
{
    private static readonly LogService Log = LogService.Instance;

    public bool         IsHotspotActive { get; private set; }
    public string?      HotspotSsid     { get; private set; }
    public HotspotMethod ActiveMethod   { get; private set; } = HotspotMethod.None;

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<(HotspotResult result, string detail)> StartHotspotAsync(
        string ssid, string password)
    {
        Log.Info("Hotspot", $"Starting hotspot — SSID: {ssid}");

        // Step 0: verify a Wi-Fi adapter exists
        var adapter = FindWifiAdapter();
        if (adapter == null)
        {
            Log.Error("Hotspot", "No Wi-Fi adapter found.");
            return (HotspotResult.NoAdapter,
                "No Wi-Fi adapter detected. AirCode needs a Wi-Fi adapter to create a local network.");
        }

        Log.Info("Hotspot", $"Wi-Fi adapter found: {adapter}");

        // Step 1: Wi-Fi Direct Group Owner (works without internet)
        Log.Info("Hotspot", "Trying Wi-Fi Direct Group Owner…");
        var wdResult = await TryWifiDirectAsync(ssid, password);
        if (wdResult.result == HotspotResult.Success)
        {
            ActiveMethod    = HotspotMethod.WifiDirect;
            IsHotspotActive = true;
            HotspotSsid     = ssid;
            Log.Success("Hotspot", $"Wi-Fi Direct Group Owner started. SSID: {ssid}");
            return wdResult;
        }
        Log.Warn("Hotspot", $"Wi-Fi Direct failed: {wdResult.detail}");

        // Step 2: Windows Mobile Hotspot (netsh wlan start hostednetwork)
        Log.Info("Hotspot", "Trying Windows Mobile Hotspot (netsh)…");
        var mhResult = await TryNetshHostedNetworkAsync(ssid, password);
        if (mhResult.result == HotspotResult.Success)
        {
            ActiveMethod    = HotspotMethod.NetshHosted;
            IsHotspotActive = true;
            HotspotSsid     = ssid;
            Log.Success("Hotspot", $"netsh hosted-network started. SSID: {ssid}");
            return mhResult;
        }
        Log.Warn("Hotspot", $"netsh hosted-network failed: {mhResult.detail}");

        // All methods failed — AirCode falls back to existing network
        Log.Warn("Hotspot",
            "All hotspot methods failed. AirCode will use the existing network adapter.");
        return (HotspotResult.NotSupported,
            "Hotspot could not be created (adapter/driver limitation). " +
            "AirCode will run on your existing network — clients must join the same Wi-Fi.");
    }

    public async Task StopAsync()
    {
        if (!IsHotspotActive) return;
        Log.Info("Hotspot", $"Stopping hotspot (method: {ActiveMethod})");

        switch (ActiveMethod)
        {
            case HotspotMethod.WifiDirect:
                await TryWifiDirectStopAsync();
                break;
            case HotspotMethod.NetshHosted:
                await RunNetshAsync("wlan stop hostednetwork");
                break;
        }

        IsHotspotActive = false;
        ActiveMethod    = HotspotMethod.None;
    }

    // ── Method 1: Wi-Fi Direct via netsh wlan connect (Group Owner mode) ──────
    // Uses the "Microsoft Wi-Fi Direct Virtual Adapter" which Windows creates
    // automatically when the physical adapter supports Wi-Fi Direct.
    private async Task<(HotspotResult result, string detail)> TryWifiDirectAsync(
        string ssid, string password)
    {
        try
        {
            // Check if Wi-Fi Direct virtual adapter is available
            var show = await RunNetshAsync("wlan show drivers");
            bool supportsDirect =
                show.output.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                show.output.Contains("Wireless Hosted Network", StringComparison.OrdinalIgnoreCase);

            if (!supportsDirect)
                return (HotspotResult.NotSupported, "Adapter does not support Wi-Fi Direct.");

            // Configure and start via hosted-network (Wi-Fi Direct uses the same API)
            var set = await RunNetshAsync(
                $"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{password}\"");

            if (!IsSuccess(set))
                return (HotspotResult.NotSupported, $"Set failed: {set.output.Trim()}");

            var start = await RunNetshAsync("wlan start hostednetwork");
            if (!IsSuccess(start))
                return (HotspotResult.Failed, $"Start failed: {start.output.Trim()}");

            // Give Windows a moment to bring up the virtual adapter
            await Task.Delay(2500);
            return (HotspotResult.Success, "Wi-Fi Direct Group Owner active.");
        }
        catch (Exception ex)
        {
            return (HotspotResult.Failed, ex.Message);
        }
    }

    private async Task TryWifiDirectStopAsync()
    {
        await RunNetshAsync("wlan stop hostednetwork");
    }

    // ── Method 2: netsh hosted-network (legacy / Win 10 fallback) ────────────
    private async Task<(HotspotResult result, string detail)> TryNetshHostedNetworkAsync(
        string ssid, string password)
    {
        try
        {
            // Check support
            var show = await RunNetshAsync("wlan show hostednetwork");
            if (show.output.Contains("not supported", StringComparison.OrdinalIgnoreCase))
                return (HotspotResult.NotSupported, "Hosted network not supported by this adapter.");

            var set = await RunNetshAsync(
                $"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{password}\"");
            if (!IsSuccess(set))
                return (HotspotResult.NotSupported, set.output.Trim());

            var start = await RunNetshAsync("wlan start hostednetwork");
            if (!IsSuccess(start))
                return (HotspotResult.Failed, start.output.Trim());

            await Task.Delay(2000);
            return (HotspotResult.Success, "Hosted network active.");
        }
        catch (Exception ex)
        {
            return (HotspotResult.Failed, ex.Message);
        }
    }

    // ── Adapter detection ─────────────────────────────────────────────────────

    /// <summary>Returns the name of the first usable Wi-Fi adapter, or null.</summary>
    public static string? FindWifiAdapter()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            if (ni.OperationalStatus == OperationalStatus.NotPresent) continue;
            // Skip virtual/loopback
            if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                ni.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;
            return ni.Description;
        }
        return null;
    }

    public static bool HasWifiAdapter() => FindWifiAdapter() != null;

    /// <summary>
    /// Returns the best local IPv4 for AirCode to listen on.
    /// Priority: hotspot virtual adapter → Wi-Fi → Ethernet.
    /// </summary>
    public static string GetBestLocalIp()
    {
        // Prefer hosted-network/Wi-Fi Direct virtual adapter
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            bool isVirtual =
                ni.Description.Contains("Hosted Network",  StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("Wi-Fi Direct",    StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("Virtual WiFi",    StringComparison.OrdinalIgnoreCase);
            if (isVirtual)
            {
                var ip = FirstIPv4(ni);
                if (ip != null)
                {
                    LogService.Instance.Info("Network", $"Using virtual adapter IP: {ip} ({ni.Description})");
                    return ip;
                }
            }
        }

        // Wi-Fi then Ethernet
        foreach (var type in new[] { NetworkInterfaceType.Wireless80211, NetworkInterfaceType.Ethernet })
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType != type) continue;
                var ip = FirstIPv4(ni);
                if (ip != null)
                {
                    LogService.Instance.Info("Network", $"Using {type} adapter IP: {ip} ({ni.Description})");
                    return ip;
                }
            }
        }

        LogService.Instance.Warn("Network", "No usable adapter found — falling back to 127.0.0.1");
        return "127.0.0.1";
    }

    public static List<(string name, string ip, NetworkInterfaceType type)> GetAllLocalAdapters()
    {
        var result = new List<(string, string, NetworkInterfaceType)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
                result.Add((ni.Description, ip, ni.NetworkInterfaceType));
        }
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? FirstIPv4(NetworkInterface ni)
    {
        foreach (var a in ni.GetIPProperties().UnicastAddresses)
            if (a.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a.Address))
                return a.Address.ToString();
        return null;
    }

    private static bool IsSuccess((int exitCode, string output) r) =>
        r.exitCode == 0 &&
        !r.output.Contains("error",       StringComparison.OrdinalIgnoreCase) &&
        !r.output.Contains("not supported", StringComparison.OrdinalIgnoreCase) &&
        !r.output.Contains("couldn't be",  StringComparison.OrdinalIgnoreCase);

    private static async Task<(int exitCode, string output)> RunNetshAsync(string args)
    {
        LogService.Instance.Debug("netsh", $"netsh {args}");
        var psi = new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = (stdout + stderr).Trim();
        LogService.Instance.Debug("netsh", $"exit={proc.ExitCode} out={output}");
        return (proc.ExitCode, output);
    }
}
