using NetworkAdapterManager.Models;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace NetworkAdapterManager.Services;

/// <summary>
/// Reads and controls network adapters on Windows using WMI (Win32_NetworkAdapter)
/// for identity/enable-disable, and System.Net.NetworkInformation for IP details.
/// </summary>
public sealed class AdapterService
{
    private static readonly IPAddress ProbeAddress = IPAddress.Parse("1.1.1.1");
    private const int ProbePort = 443;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Returns all physical/virtual adapters that Windows exposes as a network connection,
    /// sorted with Internet-capable adapters first and disconnected ones last.
    /// </summary>
    public async Task<List<NetworkAdapterInfo>> GetAdaptersAsync()
    {
        var activeLocalAddress = GetActiveLocalAddress();

        using var searcher = new ManagementObjectSearcher("SELECT Name, NetConnectionID, NetEnabled, GUID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");

        using var wmiResults = searcher.Get();
        var wmiAdapters = wmiResults.Cast<ManagementObject>().ToList();

        var nicsByGuid = NetworkInterface.GetAllNetworkInterfaces()
            .ToDictionary(nic => nic.Id, nic => nic, StringComparer.OrdinalIgnoreCase);

        // Only actively probe enabled adapters that have an IPv4 address.
        var probeTasks = new List<Task<(string Guid, bool HasInternet)>>();
        foreach (var wmiAdapter in wmiAdapters)
        {
            var guid = wmiAdapter["GUID"]?.ToString();
            var enabled = wmiAdapter["NetEnabled"] is true;

            if (guid is null || !enabled || !nicsByGuid.TryGetValue(guid, out var nic))
                continue;

            var ipv4 = GetIPv4Address(nic);
            if (ipv4 is null)
                continue;

            probeTasks.Add(ProbeInternetAsync(guid, ipv4));
        }

        var internetByGuid = (await Task.WhenAll(probeTasks))
            .ToDictionary(r => r.Guid, r => r.HasInternet, StringComparer.OrdinalIgnoreCase);

        var results = new List<NetworkAdapterInfo>();
        foreach (var wmiAdapter in wmiAdapters)
        {
            var guid = wmiAdapter["GUID"]?.ToString() ?? string.Empty;
            var name = wmiAdapter["NetConnectionID"]?.ToString() ?? "(Unknown adapter)";
            var enabled = wmiAdapter["NetEnabled"] is true;

            nicsByGuid.TryGetValue(guid, out var nic);
            var description = nic?.Description ?? "No driver information available";
            var ipv4 = GetIPv4Address(nic);

            var hasInternet = enabled && internetByGuid.GetValueOrDefault(guid, false);
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

        return results
            .OrderByDescending(a => a.HasInternet)
            .ThenByDescending(a => a.IsActive)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Makes <paramref name="target"/> the sole active adapter: enables it and
    /// disables every other adapter Windows knows about.
    /// </summary>
    public Task SwitchToAdapterAsync(NetworkAdapterInfo target) => Task.Run(() =>
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");

        using var found = searcher.Get();
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                var guid = adapter["GUID"]?.ToString();
                var isTarget = string.Equals(guid, target.Id, StringComparison.OrdinalIgnoreCase);
                adapter.InvokeMethod(isTarget ? "Enable" : "Disable", null);
            }
        }
    });

    public Task EnableAllAdaptersAsync() => SetAllAdaptersStateAsync(enable: true);

    public Task DisableAllAdaptersAsync() => SetAllAdaptersStateAsync(enable: false);

    private static Task SetAllAdaptersStateAsync(bool enable) => Task.Run(() =>
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");

        using var found = searcher.Get();
        foreach (ManagementObject adapter in found)
        {
            using (adapter)
            {
                adapter.InvokeMethod(enable ? "Enable" : "Disable", null);
            }
        }
    });

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
    /// Tests real Internet reachability from a specific adapter by binding a TCP connection
    /// to its local address and attempting to reach a public host, with a short timeout.
    /// </summary>
    private static async Task<(string Guid, bool HasInternet)> ProbeInternetAsync(string guid, IPAddress localAddress)
    {
        try
        {
            using var client = new TcpClient(new IPEndPoint(localAddress, 0));
            var connectTask = client.ConnectAsync(ProbeAddress, ProbePort);
            var timeoutTask = Task.Delay(ProbeTimeout);

            var finished = await Task.WhenAny(connectTask, timeoutTask);
            var success = finished == connectTask && client.Connected;
            return (guid, success);
        }
        catch
        {
            return (guid, false);
        }
    }

    private static IPAddress? GetIPv4Address(NetworkInterface? nic)
    {
        return nic?.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }
}