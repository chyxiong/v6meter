using System.Net;
using System.Net.Sockets;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Model;

namespace V6Meter.Core.Classification;

public sealed record ClassifierOptions(IReadOnlyList<IPNetwork> ExemptV4, IReadOnlyList<IPNetwork> Nat64Prefixes);

/// <summary>Applies the classification rules from CLAUDE.md, in order.</summary>
public static class Classifier
{
    public static TrafficClass Classify(NetEvent e, InterfaceKind localKind, ClassifierOptions options)
    {
        // 0. IPv4-mapped remotes come from dual-stack sockets and travel over IPv4.
        var remote = IpRanges.Normalize(e.RemoteAddr);
        bool isV4 = remote.AddressFamily == AddressFamily.InterNetwork;

        // 1. Loopback (e.g. a local proxy at 127.0.0.1:7890) is dropped.
        if (IpRanges.IsLoopback(remote) || localKind == InterfaceKind.Loopback)
            return new(TrafficCategory.Loopback);

        // 2. IPv6 carried inside IPv4.
        if (localKind == InterfaceKind.Tunnel4in6 || IpRanges.Teredo.Contains(remote) || IpRanges.SixToFour.Contains(remote))
            return new(TrafficCategory.V4Billed, Tunneled: true);

        // 3. Other virtual adapters, or Clash-style fake-ip range.
        if (localKind == InterfaceKind.Virtual || IpRanges.FakeIp.Contains(remote))
            return new(TrafficCategory.Virtual);

        // 4. NAT64.
        if (!isV4 && IpRanges.InAny(remote, options.Nat64Prefixes))
            return new(TrafficCategory.V6Nat64);

        // 5. Link-local / multicast / ULA / broadcast.
        if (IpRanges.IsLocalScope(remote))
            return new(TrafficCategory.Local);

        // 6. Campus / private IPv4.
        if (isV4 && IpRanges.InAny(remote, options.ExemptV4))
            return new(TrafficCategory.V4Free);

        // 7.
        return new(isV4 ? TrafficCategory.V4Billed : TrafficCategory.V6);
    }
}
