using System.Net;
using System.Net.Sockets;

namespace V6Meter.Core.Classification;

public static class IpRanges
{
    public static readonly IPNetwork Teredo = IPNetwork.Parse("2001::/32");
    public static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");
    public static readonly IPNetwork FakeIp = IPNetwork.Parse("198.18.0.0/15");

    private static readonly IPNetwork[] LocalScope =
    [
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("224.0.0.0/4"),
    ];

    /// <summary>Converts IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) to plain IPv4 and drops any scope id.</summary>
    public static IPAddress Normalize(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6)
            return a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0)
            return new IPAddress(a.GetAddressBytes());
        return a;
    }

    public static bool IsLoopback(IPAddress a) => IPAddress.IsLoopback(a);

    /// <summary>Link-local, multicast, ULA, IPv4 multicast and limited broadcast.</summary>
    public static bool IsLocalScope(IPAddress a)
    {
        if (a.Equals(IPAddress.Broadcast))
            return true;
        foreach (var n in LocalScope)
            if (n.Contains(a))
                return true;
        return false;
    }

    public static bool InAny(IPAddress a, IReadOnlyList<IPNetwork> networks)
    {
        foreach (var n in networks)
            if (n.Contains(a))
                return true;
        return false;
    }
}
