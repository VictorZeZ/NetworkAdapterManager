using NetworkAdapterManager.Models;
using System.Collections.Concurrent;
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

    // Used whenever we're about to call InvokeMethod (Enable/Disable). WMI objects fetched
    // via a narrowed column SELECT are not fully "bound" and throw InvalidOperationException
    // ("Operation is not valid due to the current state of the object") if you try to invoke
    // a method on them -- SELECT * is required for that to work reliably.
    private const string WmiAdapterMutationQuery =
        "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL";

    private static readonly IPAddress ProbeAddress = IPAddress.Parse("1.1.1.1");

    // Remembers the last HasInternet value observed for each adapter WHILE it was enabled,
    // keyed by GUID. A disabled adapter has no IP configuration left to inspect (no gateway,
    // no routes), so this is the only way to answer "did this adapter have Internet before
    // we/you disabled it?" -- it's populated every time GetAdaptersAsync sees the adapter
    // enabled, which reliably covers adapters this app itself switches away from (we always
    // scan them enabled immediately before disabling them). It resets when the app restarts,
    // and an adapter that was already disabled before the app ever saw it enabled will
    // correctly show as "unknown" rather than a guess.
    private readonly ConcurrentDictionary<string, bool> _lastKnownInternetByGuid = new(StringComparer.OrdinalIgnoreCase);

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
                // the adapter itself (set by DHCP or static config), independent of anything
                // else on the system.
                //
                // Some adapters -- most VPN tunnel clients -- never set a conventional gateway
                // at all; they add routes directly instead (often as two half-ranges covering
                // the whole address space). For those, and only when no gateway is present, we
                // fall back to asking the routing table whether this adapter's interface has a
                // route that would carry traffic to a public address.
                var hasGateway = HasDefaultGateway(nic);
                var hasRouteToInternet = ifIndex is int index && internetCapableIndexes.Contains(index);
                var hasInternet = enabled && ipv4 is not null && (hasGateway || hasRouteToInternet);

                if (enabled && !string.IsNullOrEmpty(guid))
                    _lastKnownInternetByGuid[guid] = hasInternet;

                var lastKnownHasInternet = !string.IsNullOrEmpty(guid) && _lastKnownInternetByGuid.TryGetValue(guid, out var known)
                    ? known
                    : (bool?)null;

                var isActive = enabled && activeLocalAddress is not null &&
                               ipv4 is not null && ipv4.Equals(activeLocalAddress);

                results.Add(new NetworkAdapterInfo
                {
                    Id = guid,
                    Name = name,
                    Description = description,
                    IsEnabled = enabled,
                    HasInternet = hasInternet,
                    LastKnownHasInternet = lastKnownHasInternet,
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
    /// Makes <paramref name="target"/> the sole active adapter: enables it and disables every
    /// other adapter Windows knows about. Returns whether the whole operation succeeded.
    ///
    /// The target is enabled FIRST, and we wait for it to actually come online (a real IPv4
    /// address, not just the administrative "enabled" flag) before touching anything else. If
    /// we disabled every other adapter before confirming the target was back up, a failure or
    /// delay bringing it online would leave the system with nothing enabled at all. If the
    /// target doesn't come online in time, nothing else is touched and this returns false.
    /// </summary>
    public Task<bool> SwitchToAdapterAsync(NetworkAdapterInfo target) => Task.Run(async () =>
    {
        if (!TryEnableAdapterByGuid(target.Id, out _))
            return false;

        if (!await WaitForAdapterOnlineAsync(target.Id, TimeSpan.FromSeconds(8)))
            return false;

        using var searcher = new ManagementObjectSearcher(WmiAdapterMutationQuery);
        using var found = searcher.Get();
        var allSucceeded = true;
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                var guid = adapter["GUID"]?.ToString();
                if (string.Equals(guid, target.Id, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryInvokeAdapterMethod(adapter, "Disable", out _))
                    allSucceeded = false;
            }
        }

        return allSucceeded;
    });

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
                TryInvokeAdapterMethod(adapter, enable ? "Enable" : "Disable", out _);
            }
        }
    });

    private static bool TryEnableAdapterByGuid(string guid, out string? error)
    {
        using var searcher = new ManagementObjectSearcher(WmiAdapterMutationQuery);
        using var found = searcher.Get();
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                if (string.Equals(adapter["GUID"]?.ToString(), guid, StringComparison.OrdinalIgnoreCase))
                    return TryInvokeAdapterMethod(adapter, "Enable", out error);
            }
        }

        error = "adapter not found";
        return false;
    }

    /// <summary>
    /// Calls an Enable/Disable WMI method and reports whether it actually succeeded -- both by
    /// catching exceptions AND by checking the method's ReturnValue. WMI can report a logical
    /// failure (e.g. access denied, or the adapter being mid state-change) through ReturnValue
    /// without throwing at all, so checking only for exceptions -- as earlier versions of this
    /// method did -- can silently miss a real failure and report success incorrectly.
    /// </summary>
    private static bool TryInvokeAdapterMethod(ManagementObject adapter, string methodName, out string? error)
    {
        try
        {
            using var result = adapter.InvokeMethod(methodName, null) as ManagementBaseObject;
            var returnValue = result is null ? (uint?)null : Convert.ToUInt32(result["ReturnValue"]);

            // 0 = success, 1 = success but a reboot is required. Anything else is a real
            // failure even though InvokeMethod itself didn't throw.
            if (returnValue is null or 0 or 1)
            {
                error = null;
                return true;
            }

            error = $"WMI returned code {returnValue}";
            return false;
        }
        catch (ManagementException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Waits until an adapter has a real IPv4 address -- i.e. is actually usable, not just administratively enabled.</summary>
    private static async Task<bool> WaitForAdapterOnlineAsync(string guid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, guid, StringComparison.OrdinalIgnoreCase));

            if (GetIPv4Address(nic) is not null)
                return true;

            await Task.Delay(250);
        }

        return false;
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