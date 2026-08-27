using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace AirCode.Services;

public enum HotspotResult  { Success, NotSupported, PermissionDenied, Failed, NoAdapter }
public enum HotspotMethod  { None, NetshHosted }

/// <summary>
/// Creates a local Wi-Fi hotspot via netsh wlan hosted-network.
/// Requires Administrator privileges — AirCode manifests itself as requireAdministrator.
///
/// Key design decisions from real-world testing:
///   • Skip "Microsoft Wi-Fi Direct Virtual Adapter" — it has no IP and cannot host.
///   • Bind the WebSocket server to 0.0.0.0 so clients on any subnet reach it.
///   • GetBestLocalIp() skips virtual adapters and returns the first real Wi-Fi IP.
/// </summary>
public class HotspotService
{
    private static readonly LogService Log = LogService.Instance;

    public bool        IsHotspotActive { get; private set; }
    public string?     HotspotSsid    { get; private set; }
    public HotspotMethod ActiveMethod  { get; private set; } = HotspotMethod.None;

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<(HotspotResult result, string detail)> StartHotspotAsync(
        string ssid, string password)
    {
        Log.Info("Hotspot", $"StartHotspotAsync — SSID: {ssid}");

        // Check elevation
        bool isAdmin = IsRunningAsAdmin();
        Log.Info("Hotspot", $"Running as Administrator: {isAdmin}");
        if (!isAdmin)
        {
            Log.Warn("Hotspot",
                "Not running as Administrator — netsh commands will fail. " +
                "AirCode will continue on the existing network.");
            return (HotspotResult.PermissionDenied,
                "Hotspot creation requires Administrator privileges.\n" +
                "Right-click AirCode.exe → Run as administrator, then try again.\n\n" +
                "AirCode will still work on your existing Wi-Fi — clients must connect to the same network.");
        }

        // Verify a real (non-virtual) Wi-Fi adapter is present
        var adapter = FindRealWifiAdapter();
        if (adapter == null)
        {
            Log.Error("Hotspot", "No physical Wi-Fi adapter found.");
            return (HotspotResult.NoAdapter,
                "No Wi-Fi adapter detected. AirCode needs a physical Wi-Fi adapter to create a hotspot.");
        }
        Log.Info("Hotspot", $"Physical Wi-Fi adapter: {adapter}");

        // Check if hosted-network is hardware-supported
        var showDrivers = await RunNetshAsync("wlan show drivers");
        bool hostedSupported =
            showDrivers.output.Contains("Hosted network supported  : Yes",
                StringComparison.OrdinalIgnoreCase);

        if (!hostedSupported)
        {
            Log.Warn("Hotspot",
                "Driver reports 'Hosted network supported: No'. " +
                "AirCode will use existing network.");
            return (HotspotResult.NotSupported,
                "Your Wi-Fi adapter/driver does not support hosted networks.\n" +
                "AirCode will run on your existing Wi-Fi — clients must join the same network.");
        }

        // Configure and start
        Log.Info("Hotspot", "Configuring hosted network…");
        var set = await RunNetshAsync(
            $"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{password}\"");
        if (!IsSuccess(set))
        {
            Log.Error("Hotspot", $"set hostednetwork failed: {set.output}");
            return (HotspotResult.Failed, $"Failed to configure hotspot: {set.output.Trim()}");
        }

        Log.Info("Hotspot", "Starting hosted network…");
        var start = await RunNetshAsync("wlan start hostednetwork");
        if (!IsSuccess(start))
        {
            Log.Error("Hotspot", $"start hostednetwork failed: {start.output}");
            return (HotspotResult.Failed, $"Failed to start hotspot: {start.output.Trim()}");
        }

        // Give Windows time to create the virtual adapter and assign an IP
        Log.Info("Hotspot", "Waiting for virtual adapter IP…");
        await Task.Delay(3000);

        HotspotSsid    = ssid;
        IsHotspotActive = true;
        ActiveMethod   = HotspotMethod.NetshHosted;
        Log.Success("Hotspot", $"Hotspot active. SSID: {ssid}");
        return (HotspotResult.Success, $"Hotspot created. SSID: {ssid}  Password: {password}");
    }

    public async Task StopAsync()
    {
        if (!IsHotspotActive) return;
        Log.Info("Hotspot", "Stopping hotspot…");
        await RunNetshAsync("wlan stop hostednetwork");
        IsHotspotActive = false;
        ActiveMethod   = HotspotMethod.None;
        Log.Info("Hotspot", "Hotspot stopped.");
    }

    // ── Adapter helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the description of the first *physical* Wi-Fi adapter.
    /// Skips Microsoft Wi-Fi Direct Virtual Adapter and similar virtual interfaces.
    /// </summary>
    public static string? FindRealWifiAdapter()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            if (IsVirtualAdapter(ni.Description)) continue;
            if (ni.OperationalStatus == OperationalStatus.NotPresent) continue;
            return ni.Description;
        }
        return null;
    }

    public static bool HasWifiAdapter() => FindRealWifiAdapter() != null;

    /// <summary>
    /// Returns the best local IP for the AirCode server to advertise.
    /// Priority: hotspot virtual adapter (if active + has IP) → physical Wi-Fi → Ethernet.
    /// The server itself binds to 0.0.0.0 to accept from all interfaces.
    /// </summary>
    public static string GetBestLocalIp()
    {
        // 1. Hosted-network / hotspot virtual adapter (only if it has an IP)
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            bool isHotspot =
                ni.Description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase) ||
                (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                 ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            if (!isHotspot) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
            {
                Log.Info("Network", $"Hotspot adapter IP: {ip} ({ni.Description})");
                return ip;
            }
        }

        // 2. Physical Wi-Fi (skip virtual/Direct adapters)
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            if (IsVirtualAdapter(ni.Description)) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
            {
                Log.Info("Network", $"Wi-Fi adapter IP: {ip} ({ni.Description})");
                return ip;
            }
        }

        // 3. Ethernet
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
            var ip = FirstIPv4(ni);
            if (ip != null)
            {
                Log.Info("Network", $"Ethernet adapter IP: {ip} ({ni.Description})");
                return ip;
            }
        }

        Log.Warn("Network", "No usable adapter found — clients must connect manually.");
        return "0.0.0.0";
    }

    public static List<(string name, string ip, NetworkInterfaceType type)> GetAllLocalAdapters()
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

    // ── Elevation check ───────────────────────────────────────────────────────

    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Re-launches AirCode.exe with admin rights and exits the current process.</summary>
    public static void RestartAsAdmin()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "AirCode.exe";
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb            = "runas"
        };
        try { Process.Start(psi); }
        catch { /* user cancelled UAC */ return; }
        System.Windows.Application.Current.Shutdown();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsVirtualAdapter(string description) =>
        description.Contains("Wi-Fi Direct",    StringComparison.OrdinalIgnoreCase) ||
        description.Contains("Virtual WiFi",    StringComparison.OrdinalIgnoreCase) ||
        description.Contains("Microsoft Virtual", StringComparison.OrdinalIgnoreCase);

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
        !r.output.Contains("administrator", StringComparison.OrdinalIgnoreCase) &&
        !r.output.Contains("not supported",  StringComparison.OrdinalIgnoreCase) &&
        !r.output.Contains("couldn't be",   StringComparison.OrdinalIgnoreCase) &&
        !r.output.Contains("failed",        StringComparison.OrdinalIgnoreCase);

    private static async Task<(int exitCode, string output)> RunNetshAsync(string args)
    {
        Log.Debug("netsh", $"netsh {args}");
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
        Log.Debug("netsh", $"exit={proc.ExitCode} out={output}");
        return (proc.ExitCode, output);
    }
}
