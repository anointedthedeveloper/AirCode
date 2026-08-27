using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirCode.Services;

public enum HotspotResult { Success, NotSupported, PermissionDenied, Failed }

/// <summary>
/// Manages Wi-Fi hotspot creation via Windows Mobile Hotspot (netsh / WlanAPI).
/// Falls back gracefully on unsupported hardware/drivers.
/// </summary>
public class HotspotService
{
    public bool IsHotspotActive { get; private set; }
    public string? HotspotSsid { get; private set; }
    public string? HotspotPassword { get; private set; }

    /// <summary>
    /// Attempts to create a Windows Mobile Hotspot using netsh.
    /// Requires administrator privileges and a compatible Wi-Fi adapter.
    /// </summary>
    public async Task<HotspotResult> StartHotspotAsync(string ssid, string password)
    {
        try
        {
            // Step 1 – set the hosted network parameters
            var setResult = await RunNetshAsync(
                $"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{password}\"");
            if (setResult.exitCode != 0)
                return HotspotResult.NotSupported;

            // Step 2 – start the hosted network
            var startResult = await RunNetshAsync("wlan start hostednetwork");
            if (startResult.exitCode != 0)
            {
                // Check common failure reason
                if (startResult.output.Contains("hosted network couldn't be started") ||
                    startResult.output.Contains("not supported"))
                    return HotspotResult.NotSupported;
                return HotspotResult.Failed;
            }

            HotspotSsid = ssid;
            HotspotPassword = password;
            IsHotspotActive = true;
            return HotspotResult.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return HotspotResult.PermissionDenied;
        }
        catch
        {
            return HotspotResult.Failed;
        }
    }

    public async Task<bool> StopHotspotAsync()
    {
        var result = await RunNetshAsync("wlan stop hostednetwork");
        IsHotspotActive = false;
        return result.exitCode == 0;
    }

    public async Task<string?> GetHotspotStatusAsync()
    {
        var result = await RunNetshAsync("wlan show hostednetwork");
        return result.output;
    }

    /// <summary>
    /// Finds the best local IP address for the AirCode server.
    /// On hotspot: returns the virtual adapter IP (usually 192.168.137.x).
    /// On regular Wi-Fi/LAN: returns the first non-loopback IPv4.
    /// </summary>
    public static string GetBestLocalIp()
    {
        // Prefer Wi-Fi hotspot adapter (Microsoft Hosted Network Virtual Adapter)
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            // Hosted network virtual adapter typically has this in the description
            bool isHosted = ni.Description.Contains("Microsoft Hosted Network", StringComparison.OrdinalIgnoreCase)
                         || ni.Description.Contains("Virtual WiFi", StringComparison.OrdinalIgnoreCase)
                         || ni.Description.Contains("Microsoft Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase);

            if (isHosted)
            {
                var ip = GetFirstIPv4(ni);
                if (ip != null) return ip;
            }
        }

        // Fall back to first non-loopback Wi-Fi or Ethernet
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                                        or NetworkInterfaceType.Ethernet)
            {
                var ip = GetFirstIPv4(ni);
                if (ip != null) return ip;
            }
        }

        return "127.0.0.1";
    }

    private static string? GetFirstIPv4(NetworkInterface ni)
    {
        foreach (var addr in ni.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(addr.Address))
                return addr.Address.ToString();
        }
        return null;
    }

    private static async Task<(int exitCode, string output)> RunNetshAsync(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, output);
    }
}
