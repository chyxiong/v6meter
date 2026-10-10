using System.Net.NetworkInformation;

namespace V6Meter.Core.Interfaces;

public enum InterfaceKind
{
    /// <summary>Address not found in the current map (e.g. a fresh IPv6 temporary address). Treated like Physical.</summary>
    Unknown,
    Physical,
    /// <summary>Teredo, 6to4, IP-HTTPS: IPv6 carried over IPv4.</summary>
    Tunnel4in6,
    /// <summary>TUN/VPN/Wintun/WireGuard/Hyper-V/VMware/VirtualBox and anything else without a physical connector.</summary>
    Virtual,
    Loopback,
}

public sealed record InterfaceInfo(int Index, string Name, string Description, InterfaceKind Kind, bool IsHyperVExternal = false);

/// <summary>What we know about an adapter when deciding its kind.</summary>
/// <param name="ConnectorPresent">From MSFT_NetAdapter (same source as Get-NetAdapter -Physical); null if the adapter is not listed there.</param>
public sealed record AdapterFacts(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool? ConnectorPresent,
    bool HasIPv4DefaultGateway);

public static class InterfaceKindRules
{
    private static readonly string[] TunnelMarkers = ["teredo", "6to4", "ip-https", "iphttps"];

    public static (InterfaceKind Kind, bool IsHyperVExternal) Decide(AdapterFacts f)
    {
        if (f.Type == NetworkInterfaceType.Loopback)
            return (InterfaceKind.Loopback, false);

        if (f.Type == NetworkInterfaceType.Tunnel || f.ConnectorPresent is null)
        {
            string text = (f.Name + " " + f.Description).ToLowerInvariant();
            if (TunnelMarkers.Any(text.Contains))
                return (InterfaceKind.Tunnel4in6, false);
        }

        if (f.ConnectorPresent == true)
            return (InterfaceKind.Physical, false);

        // A physical NIC bound to a Hyper-V external switch loses its IP config to the host vNIC,
        // which then carries the default gateway. Count it as physical and flag it for the UI.
        if (IsHyperVHostVnic(f.Description) && f.HasIPv4DefaultGateway)
            return (InterfaceKind.Physical, true);

        return (InterfaceKind.Virtual, false);
    }

    /// <summary>
    /// Stand-in for ConnectorPresent when the CIM query fails and the adapter was never seen: a wired or
    /// Wi-Fi adapter holding the IPv4 default route is most likely the real uplink. Otherwise unknown (null).
    /// </summary>
    public static bool? GuessConnectorPresent(NetworkInterfaceType type, bool hasIPv4DefaultGateway) =>
        (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211) && hasIPv4DefaultGateway
            ? true : null;

    private static bool IsHyperVHostVnic(string description) =>
        description.StartsWith("Hyper-V Virtual Ethernet Adapter", StringComparison.OrdinalIgnoreCase);
}
