using System.Runtime.InteropServices;

namespace V6Meter.Core.Estimation;

/// <summary>Per-family IP datagram counters (32-bit, wrap around) from GetIpStatisticsEx.</summary>
public readonly record struct IpFamilyCounters(uint InReceives, uint InDelivers, uint OutRequests)
{
    public static bool TryRead(bool ipv6, out IpFamilyCounters c)
    {
        if (GetIpStatisticsEx(out var s, ipv6 ? AF_INET6 : AF_INET) == 0)
        {
            c = new IpFamilyCounters(s.InReceives, s.InDelivers, s.OutRequests);
            return true;
        }
        c = default;
        return false;
    }

    private const uint AF_INET = 2, AF_INET6 = 23;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpStats
    {
        public uint Forwarding, DefaultTTL, InReceives, InHdrErrors, InAddrErrors, ForwDatagrams, InUnknownProtos, InDiscards,
            InDelivers, OutRequests, RoutingDiscards, OutDiscards, OutNoRoutes, ReasmTimeout, ReasmReqds, ReasmOks, ReasmFails,
            FragOks, FragFails, FragCreates, NumIf, NumAddr, NumRoutes;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpStatisticsEx(out MibIpStats stats, uint family);
}

/// <summary>Estimated per-family bytes for one second. In = download, Out = upload.</summary>
public readonly record struct FamilySplit(long V4In, long V4Out, long V6In, long V6Out);

/// <summary>
/// User mode (no admin, no ETW): splits physical NIC byte deltas between IPv4 and IPv6 in proportion
/// to each family's datagram counts.
/// Download uses InReceives, which excludes loopback (verified: a local proxy does not skew it).
/// Upload uses OutRequests − (InDelivers − InReceives): loopback datagrams appear in both OutRequests and
/// InDelivers but not in InReceives, so the difference removes them. NIC receive coalescing makes datagram
/// counts unusable as sizes, which is why only the ratio is used.
/// </summary>
public sealed class FamilySplitEstimator
{
    private IpFamilyCounters? _last4, _last6;

    public FamilySplit Update(IpFamilyCounters v4, IpFamilyCounters v6, long nicIn, long nicOut)
    {
        var (p4, p6) = (_last4, _last6);
        (_last4, _last6) = (v4, v6);
        if (p4 is not { } a4 || p6 is not { } a6)
            return default;

        long in4 = Delta(a4.InReceives, v4.InReceives), in6 = Delta(a6.InReceives, v6.InReceives);
        long out4 = NonLoopbackOut(a4, v4), out6 = NonLoopbackOut(a6, v6);
        var (d4, d6) = Split(nicIn, in4, in6);
        var (u4, u6) = Split(nicOut, out4, out6);
        return new FamilySplit(d4, u4, d6, u6);
    }

    private static long NonLoopbackOut(IpFamilyCounters prev, IpFamilyCounters cur)
    {
        long loopback = Math.Max(0, Delta(prev.InDelivers, cur.InDelivers) - Delta(prev.InReceives, cur.InReceives));
        return Math.Max(0, Delta(prev.OutRequests, cur.OutRequests) - loopback);
    }

    /// <summary>Unsigned 32-bit difference, so a counter wrap still yields the right delta.</summary>
    private static long Delta(uint prev, uint cur) => unchecked(cur - prev);

    private static (long V4, long V6) Split(long bytes, long packets4, long packets6)
    {
        if (bytes <= 0 || packets4 + packets6 == 0)
            return (0, 0);
        long v4 = (long)Math.Round(bytes * (double)packets4 / (packets4 + packets6));
        return (v4, bytes - v4);
    }
}
