using NetworkAdapterManager.Models;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace NetworkAdapterManager.Services;

/// <summary>
/// Reads and controls network adapters on Windows using WMI (Win32_NetworkAdapter) for
/// identity/enable-disable, the Windows routing table for connectivity, and
/// System.Net.NetworkInformation for IP details.
/// </summary>
public sealed class AdapterService
{
    // Used for reading adapter info. A narrow column list is faster to enumerate, but WMI
    // objects returned from it must NOT have InvokeMethod called on them -- see below.
    private const string WmiAdapterQuery =
        "SELECT Name, NetConnectionID, NetEnabled, GUID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL";

    // Used anywhere we're about to call InvokeMethod (Enable/Disable). WMI objects fetched
    // via a narrowed column SELECT are not fully "bound" and throw InvalidOperationException
    // ("Operation is not valid due to the current state of the object") if you try to invoke
    // a method on them -- SELECT * is required for that to work reliably.
    private const string WmiAdapterMutationQuery =
        "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL";

    // Windows interface metrics: lower wins. 1 is effectively "most preferred"; 9999 is the
    // practical ceiling, well above any automatically-computed metric, so it always loses.
    private const int PreferredMetric = 1;
    private const int DeprioritizedMetric = 9999;

    private static readonly IPAddress ProbeAddress = IPAddress.Parse("1.1.1.1");

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Returns all adapters that Windows exposes as a network connection, sorted with
    /// Internet-capable adapters first and disconnected ones last.
    /// </summary>
    public Task<List<NetworkAdapterInfo>> GetAdaptersAsync() => Task.Run(() =>
    {
        var activeLocalAddress = GetActiveLocalAddress();
        var internetCapableIndexes = GetInternetCapableInterfaceIndexes();

        using var searcher = new ManagementObjectSearcher(WmiAdapterQuery);
        using var wmiResults = searcher.Get();
        var wmiAdapters = wmiResults.Cast<ManagementObject>().ToList();

        var nicsByGuid = NetworkInterface.GetAllNetworkInterfaces()
            .ToDictionary(nic => nic.Id, nic => nic, StringComparer.OrdinalIgnoreCase);

        var results = new List<NetworkAdapterInfo>();
        foreach (var wmiAdapter in wmiAdapters)
        {
            using (wmiAdapter)
            {
                var guid = wmiAdapter["GUID"]?.ToString() ?? string.Empty;
                var name = wmiAdapter["NetConnectionID"]?.ToString() ?? "(Unknown adapter)";
                var enabled = wmiAdapter["NetEnabled"] is true;

                nicsByGuid.TryGetValue(guid, out var nic);
                var description = nic?.Description ?? "No driver information available";
                var ipv4 = GetIPv4Address(nic);
                var ifIndex = GetIPv4InterfaceIndex(nic);

                // "Has Internet" is primarily read from THIS adapter's own IP configuration --
                // does it have a real IPv4 address and a default gateway? That is intrinsic to
                // the adapter itself (set by DHCP or static config) and does NOT change based
                // on interface metric or which adapter Windows currently prefers, so it stays
                // correct no matter how many times you switch back and forth between two
                // physically-connected adapters.
                //
                // Some adapters -- most VPN tunnel clients -- never set a conventional gateway
                // at all; they add routes directly instead (often as two half-ranges covering
                // the whole address space). For those, and only when no gateway is present, we
                // fall back to asking the routing table whether this adapter's interface has a
                // route that would carry traffic to a public address.
                var hasGateway = HasDefaultGateway(nic);
                var hasRouteToInternet = ifIndex is int index && internetCapableIndexes.Contains(index);
                var hasInternet = enabled && ipv4 is not null && (hasGateway || hasRouteToInternet);

                var isActive = enabled && activeLocalAddress is not null &&
                               ipv4 is not null && ipv4.Equals(activeLocalAddress);

                results.Add(new NetworkAdapterInfo
                {
                    Id = guid,
                    Name = name,
                    Description = description,
                    IsEnabled = enabled,
                    HasInternet = hasInternet,
                    IsActive = isActive,
                    IPv4Address = ipv4?.ToString()
                });
            }
        }

        return results
            .OrderByDescending(a => a.HasInternet)
            .ThenByDescending(a => a.IsActive)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    /// <summary>
    /// Makes <paramref name="target"/> the system's preferred adapter for outbound traffic by
    /// giving it the lowest interface metric and deprioritizing every other adapter. Unlike an
    /// earlier version of this method, it does NOT disable other adapters -- on a machine with
    /// two independent Internet connections (e.g. a wired modem and a tethered phone), both
    /// should stay connected; only which one Windows prefers should change. Returns false if
    /// any of the underlying `netsh` calls failed (most commonly because the app isn't
    /// elevated).
    /// </summary>
    public async Task<bool> SwitchToAdapterAsync(NetworkAdapterInfo target)
    {
        if (!target.IsEnabled)
        {
            await EnableAdapterAsync(target.Id);
            await WaitForAdapterReadyAsync(target.Id, TimeSpan.FromSeconds(5));
        }

        var success = true;
        foreach (var name in GetManagedAdapterNames())
        {
            var metric = string.Equals(name, target.Name, StringComparison.OrdinalIgnoreCase)
                ? PreferredMetric
                : DeprioritizedMetric;

            if (!await SetInterfaceMetricAsync(name, metric))
                success = false;
        }

        return success;
    }

    public Task EnableAllAdaptersAsync() => SetAllAdaptersStateAsync(enable: true);

    public Task DisableAllAdaptersAsync() => SetAllAdaptersStateAsync(enable: false);

    private static Task SetAllAdaptersStateAsync(bool enable) => Task.Run(() =>
    {
        using var searcher = new ManagementObjectSearcher(WmiAdapterMutationQuery);
        using var found = searcher.Get();
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                TryInvokeAdapterMethod(adapter, enable ? "Enable" : "Disable");
            }
        }
    });

    private static Task EnableAdapterAsync(string guid) => Task.Run(() =>
    {
        using var searcher = new ManagementObjectSearcher(WmiAdapterMutationQuery);
        using var found = searcher.Get();
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                if (string.Equals(adapter["GUID"]?.ToString(), guid, StringComparison.OrdinalIgnoreCase))
                {
                    TryInvokeAdapterMethod(adapter, "Enable");
                    break;
                }
            }
        }
    });

    /// <summary>
    /// Calls an Enable/Disable WMI method and swallows failures instead of letting them
    /// propagate -- a single adapter that WMI can't currently operate on (e.g. mid state
    /// change, or a virtual adapter that doesn't support the operation) should not crash
    /// the whole app.
    /// </summary>
    private static void TryInvokeAdapterMethod(ManagementObject adapter, string methodName)
    {
        try
        {
            adapter.InvokeMethod(methodName, null);
        }
        catch (ManagementException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Waits until an adapter reappears at the IP level after being enabled.</summary>
    private static async Task WaitForAdapterReadyAsync(string guid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ready = NetworkInterface.GetAllNetworkInterfaces()
                .Any(nic => string.Equals(nic.Id, guid, StringComparison.OrdinalIgnoreCase));
            if (ready)
                return;

            await Task.Delay(250);
        }
    }

    private static List<string> GetManagedAdapterNames()
    {
        using var searcher = new ManagementObjectSearcher(WmiAdapterQuery);
        using var found = searcher.Get();

        var names = new List<string>();
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                var name = adapter["NetConnectionID"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Sets an adapter's IPv4 interface metric via netsh. Requires Administrator.</summary>
    private static async Task<bool> SetInterfaceMetricAsync(string interfaceName, int metric)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ipv4 set interface interface=\"{interfaceName}\" metric={metric}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines the local IPv4 address currently used for outbound traffic by opening
    /// a UDP "connection" (no packets are actually sent) to a public address and reading
    /// back the local endpoint the OS routing table selected.
    /// </summary>
    private static IPAddress? GetActiveLocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(ProbeAddress, 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the live IPv4 routing table (via MSFT_NetRoute) and returns the set of interface
    /// indexes that have a route covering <see cref="ProbeAddress"/> -- i.e. adapters that are
    /// actually configured to carry traffic to the public internet, regardless of how that
    /// route was installed (DHCP gateway, static route, or a VPN client's split-tunnel routes).
    /// </summary>
    private static HashSet<int> GetInternetCapableInterfaceIndexes()
    {
        var indexes = new HashSet<int>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2",
                "SELECT InterfaceIndex, DestinationPrefix FROM MSFT_NetRoute WHERE AddressFamily = 2");

            using var routes = searcher.Get();
            foreach (ManagementBaseObject route in routes)
            {
                using (route)
                {
                    var prefixText = route["DestinationPrefix"]?.ToString();
                    var ifIndexValue = route["InterfaceIndex"];

                    if (string.IsNullOrEmpty(prefixText) || ifIndexValue is null)
                        continue;

                    if (!IPNetwork.TryParse(prefixText, out var network))
                        continue;

                    if (network.Contains(ProbeAddress))
                        indexes.Add(Convert.ToInt32(ifIndexValue));
                }
            }
        }
        catch (ManagementException)
        {
            // MSFT_NetRoute unavailable on this system -- adapters will conservatively show
            // as offline rather than the app guessing incorrectly.
        }

        return indexes;
    }

    private static bool HasDefaultGateway(NetworkInterface? nic)
    {
        return nic?.GetIPProperties().GatewayAddresses
            .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
            ?? false;
    }

    private static int? GetIPv4InterfaceIndex(NetworkInterface? nic)
    {
        if (nic is null)
            return null;

        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static IPAddress? GetIPv4Address(NetworkInterface? nic)
    {
        return nic?.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }
}