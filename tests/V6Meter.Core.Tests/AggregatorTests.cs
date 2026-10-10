using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using V6Meter.Core.Aggregation;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Model;
using V6Meter.Core.Processes;

namespace V6Meter.Core.Tests;

public class AggregatorTests
{
    private sealed class FakeResolver(Dictionary<uint, ProcessIdentity> ids) : IProcessResolver
    {
        public ProcessIdentity Resolve(uint pid, long second = long.MaxValue) => ids.TryGetValue(pid, out var id) ? id : ProcessNaming.Unknown(pid);
    }

    private static readonly InterfaceInfo Eth = new(7, "以太网", "Realtek", InterfaceKind.Physical);
    private static readonly InterfaceInfo Zt = new(25, "ZeroTier", "ZeroTier Virtual Port", InterfaceKind.Virtual);

    private static readonly InterfaceMap Map = new([
        (Eth, [IPAddress.Parse("192.168.100.111"), IPAddress.Parse("fd00:1::e83")]),
        (Zt, [IPAddress.Parse("192.168.195.91")]),
        (new InterfaceInfo(1, "Loopback", "Software Loopback Interface 1", InterfaceKind.Loopback), [IPAddress.Loopback, IPAddress.IPv6Loopback]),
    ]);

    private static readonly FakeResolver Resolver = new(new()
    {
        [100] = new ProcessIdentity(@"c:\windows\system32\curl.exe", "curl", @"C:\Windows\System32\curl.exe"),
        [200] = new ProcessIdentity(@"c:\clash\verge-mihomo.exe", "verge-mihomo.exe", @"C:\clash\verge-mihomo.exe"),
        [201] = new ProcessIdentity(@"c:\windows\system32\curl.exe", "curl", @"C:\Windows\System32\curl.exe"), // second curl process
    });

    private static UInt128 Raw(string ip)
    {
        Span<byte> b = stackalloc byte[16];
        b.Clear();
        IPAddress.Parse(ip).GetAddressBytes().CopyTo(b);
        return MemoryMarshal.Read<UInt128>(b);
    }

    private static KeyValuePair<FlowKey, long> Flow(uint pid, NetDirection dir, string s, string d, long bytes) =>
        new(new FlowKey(pid, dir, s.Contains(':'), Raw(s), Raw(d)), bytes);

    private static Aggregator NewAggregator() => new(new Config.AppConfig().ToClassifierOptions(out _));

    [Fact]
    public void ClassifiesAndSumsPerProcessByExePath()
    {
        var agg = NewAggregator();
        var r = agg.AddSecond([
            Flow(100, NetDirection.Recv, "192.168.100.111", "101.6.15.130", 10_000),
            Flow(201, NetDirection.Recv, "fd00:1::e83", "2402:f000:1:400::2", 20_000),
            Flow(100, NetDirection.Send, "192.168.100.111", "101.6.15.130", 300),
            Flow(100, NetDirection.Recv, "192.168.100.111", "192.168.100.1", 50),
        ], Map, Resolver);

        Assert.Equal(10_000, r.Traffic[TrafficCategory.V4Billed, NetDirection.Recv]);
        Assert.Equal(50, r.Traffic[TrafficCategory.V4Free, NetDirection.Recv]);
        Assert.Equal(20_000, r.Traffic[TrafficCategory.V6, NetDirection.Recv]);
        Assert.Equal(300, r.Traffic[TrafficCategory.V4Billed, NetDirection.Send]);

        var curl = Assert.Single(agg.Processes);
        Assert.Equal(10_050, curl.Total.V4(NetDirection.Recv));
        Assert.Equal(20_000, curl.Total.V6(NetDirection.Recv));
    }

    [Fact]
    public void LoopbackIsDroppedEverywhereAndProxyCarriesTheTraffic()
    {
        var agg = NewAggregator();
        var r = agg.AddSecond([
            Flow(100, NetDirection.Recv, "127.0.0.1", "127.0.0.1", 10_000),          // curl ← local proxy
            Flow(200, NetDirection.Send, "127.0.0.1", "127.0.0.1", 10_000),          // proxy → curl
            Flow(200, NetDirection.Recv, "192.168.100.111", "101.6.15.130", 10_100), // proxy ← internet
        ], Map, Resolver);

        Assert.Equal(0, r.Traffic[TrafficCategory.Loopback, NetDirection.Recv]);
        var proxy = Assert.Single(agg.Processes);
        Assert.Equal("verge-mihomo.exe", proxy.Identity.DisplayName);
        Assert.Equal(10_100, proxy.Total[TrafficCategory.V4Billed, NetDirection.Recv]);
        Assert.Equal(new InterfaceBytes(10_100, 0), r.EtwByInterface[7]);
        Assert.False(r.EtwByInterface.ContainsKey(1));
    }

    [Fact]
    public void LocalEndIsDetectedEvenIfSwapped()
    {
        var agg = NewAggregator();
        var r = agg.AddSecond([Flow(100, NetDirection.Recv, "101.6.15.130", "192.168.100.111", 1000)], Map, Resolver);
        Assert.Equal(1000, r.Traffic[TrafficCategory.V4Billed, NetDirection.Recv]);
        Assert.Equal(0, r.FlowsWithUnknownLocal);
        Assert.Equal(new InterfaceBytes(1000, 0), r.EtwByInterface[7]);
    }

    [Fact]
    public void VirtualInterfaceTrafficIsSeparateAndNotOnPhysicalNic()
    {
        var agg = NewAggregator();
        var r = agg.AddSecond([Flow(100, NetDirection.Recv, "192.168.195.91", "101.6.15.130", 1000)], Map, Resolver);
        Assert.Equal(1000, r.Traffic[TrafficCategory.Virtual, NetDirection.Recv]);
        Assert.Equal(0, r.Traffic.V4(NetDirection.Recv));
        Assert.Equal(new InterfaceBytes(1000, 0), r.EtwByInterface[25]);
        Assert.False(r.EtwByInterface.ContainsKey(7));
    }

    [Fact]
    public void UnknownLocalAddressIsCountedAndReported()
    {
        var agg = NewAggregator();
        var r = agg.AddSecond([Flow(100, NetDirection.Recv, "fd00:1::abcd", "2402:f000:1:400::2", 1000)], Map, Resolver);
        Assert.Equal(1000, r.Traffic.V6(NetDirection.Recv));
        Assert.Equal(1, r.FlowsWithUnknownLocal);
    }

    [Fact]
    public void LastSecondResetsWhileTotalsAccumulate()
    {
        var agg = NewAggregator();
        agg.AddSecond([Flow(100, NetDirection.Recv, "192.168.100.111", "101.6.15.130", 1000)], Map, Resolver);
        agg.AddSecond([], Map, Resolver);
        var curl = Assert.Single(agg.Processes);
        Assert.Equal(0, curl.LastSecond.V4(NetDirection.Recv));
        Assert.Equal(1000, curl.Total.V4(NetDirection.Recv));
        Assert.Equal(1000, agg.Total.V4(NetDirection.Recv));
    }

    [Fact]
    public void TunneledBytesAreTrackedInsideBilled()
    {
        var agg = NewAggregator();
        var r = agg.AddSecond([Flow(100, NetDirection.Recv, "fd00:1::e83", "2002:6506:f82::1", 700)], Map, Resolver);
        Assert.Equal(700, r.Traffic[TrafficCategory.V4Billed, NetDirection.Recv]);
        Assert.Equal(700, r.Traffic.Tunneled(NetDirection.Recv));
        Assert.Equal(700, r.Traffic.V4(NetDirection.Recv));
    }

    [Fact]
    public void FlowAccumulatorSumsPerFlowAndSkipsZeroSize()
    {
        var acc = new FlowAccumulator();
        var e = new KernelNetEvent
        {
            EventId = 11, HeaderPid = 4, Pid = 100, Size = 1440, IsV6 = false, Proto = NetProto.Tcp,
            Direction = NetDirection.Recv, SaddrRaw = Raw("192.168.100.111"), DaddrRaw = Raw("101.6.15.130"), Sport = 1, Dport = 443,
        };
        long t = 1000 * Stopwatch.Frequency; // second 1000
        acc.Add(e with { TimestampQpc = t });
        acc.Add(e with { TimestampQpc = t + Stopwatch.Frequency / 2 });
        acc.Add(e with { TimestampQpc = t, Size = 0 });
        acc.Add(e with { TimestampQpc = t, Sport = 2 }); // ports are not part of the flow key
        Assert.Equal(4320, Assert.Single(acc.Take(1000)).Value);
        Assert.Empty(acc.Take(1001));
        Assert.Equal(3, acc.TotalEvents);
    }

    private static readonly FlowKey KeyA = new(1, NetDirection.Recv, false, 1, 2);
    private static readonly FlowKey KeyB = new(2, NetDirection.Recv, true, 3, 4);

    [Fact]
    public void EventsGoToTheSecondOfTheirTimestampNotOfArrival()
    {
        // Delivered in one late batch, but they happened in two different seconds.
        var acc = new FlowAccumulator();
        acc.Add(10, KeyA, 100);
        acc.Add(11, KeyA, 200);
        acc.Add(10, KeyB, 50);
        var s10 = acc.Take(10);
        Assert.Equal(100, s10[KeyA]);
        Assert.Equal(50, s10[KeyB]);
        Assert.Equal(200, acc.Take(11)[KeyA]);
    }

    [Fact]
    public void LateEventsForAClosedSecondCountInTheNextOne()
    {
        var acc = new FlowAccumulator();
        acc.Add(10, KeyA, 100);
        Assert.Equal(100, acc.Take(10)[KeyA]);
        acc.Add(9, KeyA, 7);   // arrived after second 10 was closed
        acc.Add(10, KeyA, 5);
        acc.Add(11, KeyA, 300);
        Assert.Equal(312, acc.Take(11)[KeyA]); // nothing is lost
    }

    [Fact]
    public void QuietSecondsAreEmptyAndSkippedSecondsAreMerged()
    {
        var acc = new FlowAccumulator();
        Assert.Empty(acc.Take(5));
        acc.Add(6, KeyA, 1);
        acc.Add(8, KeyA, 2);
        acc.Add(9, KeyA, 4);
        Assert.Equal(7, acc.Take(9)[KeyA]); // catching up after a gap merges 6..9 instead of dropping them
        Assert.Empty(acc.Take(10));
    }
}
