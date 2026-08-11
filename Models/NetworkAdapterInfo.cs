namespace NetworkAdapterManager.Models;

/// <summary>
/// Snapshot of a single network adapter, combining WMI (enabled state, identity)
/// and .NET NetworkInterface (description, IP address) information.
/// </summary>
public sealed record NetworkAdapterInfo
{
    /// <summary>WMI adapter GUID. Used internally to enable/disable the adapter.</summary>
    public required string Id { get; init; }

    /// <summary>Friendly connection name, e.g. "Ethernet" or "Wi-Fi".</summary>
    public required string Name { get; init; }

    /// <summary>Hardware/driver description, e.g. "Intel(R) Wi-Fi 6 AX201".</summary>
    public required string Description { get; init; }

    /// <summary>Whether the adapter is currently enabled at the OS level.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>Whether this adapter currently has working Internet access.</summary>
    public required bool HasInternet { get; init; }

    /// <summary>
    /// The last HasInternet value observed for this adapter while it was enabled, or null if
    /// it has never been seen enabled during this app session. Only meaningful when the
    /// adapter is currently disabled -- a disabled adapter has no IP configuration to read,
    /// so this is what lets the switch menu show "this one is off, but it did have Internet"
    /// instead of treating every disabled adapter identically.
    /// </summary>
    public bool? LastKnownHasInternet { get; init; }

    /// <summary>Whether this adapter is the one the system currently uses for outbound traffic.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Current IPv4 address, if any.</summary>
    public string? IPv4Address { get; init; }
}