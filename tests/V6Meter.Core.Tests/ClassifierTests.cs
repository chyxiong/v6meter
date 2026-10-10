using System.Net;
using V6Meter.Core.Classification;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Model;

namespace V6Meter.Core.Tests;

public class ClassifierTests
{
    private static readonly ClassifierOptions Defaults = new Config.AppConfig().ToClassifierOptions(out _);

    private static TrafficClass Classify(string local, string remote, InterfaceKind kind = InterfaceKind.Physical, ClassifierOptions? options = null)
    {
        var l = IPAddress.Parse(local);
        var e = new NetEvent(1234, NetDirection.Recv, l.AddressFamily, l, IPAddress.Parse(remote), 1000);
        return Classifier.Classify(e, kind, options ?? Defaults);
    }

    [Theory]
    [InlineData("::ffff:192.168.1.10", "::ffff:101.6.15.130", TrafficCategory.V4Billed)]
    [InlineData("::ffff:192.168.1.10", "::ffff:10.1.2.3", TrafficCategory.V4Free)]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1", TrafficCategory.Loopback)]
    public void Rule0_MappedAddressesAreTreatedAsIPv4(string local, string remote, TrafficCategory expected) =>
        Assert.Equal(expected, Classify(local, remote).Category);

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.8.9.10")]
    [InlineData("::1", "::1")]
    public void Rule1_LoopbackIsDropped(string local, string remote) =>
        Assert.Equal(TrafficCategory.Loopback, Classify(local, remote).Category);

    [Fact]
    public void Rule1_LoopbackWinsOverVirtualInterface() =>
        Assert.Equal(TrafficCategory.Loopback, Classify("127.0.0.1", "127.0.0.1", InterfaceKind.Virtual).Category);

    [Fact]
    public void Rule2_TunnelInterfaceIsBilledAndTunneled()
    {
        var c = Classify("2001:0:4136:e378::1", "2402:f000:1:400::2", InterfaceKind.Tunnel4in6);
        Assert.Equal(new TrafficClass(TrafficCategory.V4Billed, Tunneled: true), c);
    }

    [Theory]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("2002:6506:f82::1")]                      // 6to4
    public void Rule2_TunnelRemoteRangesAreBilled(string remote) =>
        Assert.Equal(new TrafficClass(TrafficCategory.V4Billed, true), Classify("2402:f000:5:1::10", remote));

    [Theory]
    [InlineData("2001:da8:8000::1")]  // CERNET2 — inside 2001::/16 but NOT 2001::/32
    [InlineData("2001:250:1::1")]     // CERNET
    [InlineData("2001:db8::1")]
    public void Rule2_CernetIsNotMistakenForTeredo(string remote) =>
        Assert.Equal(TrafficCategory.V6, Classify("2402:f000:5:1::10", remote).Category);

    [Fact]
    public void Rule3_VirtualInterfaceIsVirtual() =>
        Assert.Equal(TrafficCategory.Virtual, Classify("192.168.195.91", "101.6.15.130", InterfaceKind.Virtual).Category);

    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    public void Rule3_FakeIpRangeIsVirtual(string remote) =>
        Assert.Equal(TrafficCategory.Virtual, Classify("192.168.100.111", remote).Category);

    [Fact]
    public void Rule3_JustOutsideFakeIpIsBilled() =>
        Assert.Equal(TrafficCategory.V4Billed, Classify("192.168.100.111", "198.20.0.1").Category);

    [Fact]
    public void Rule3_TunnelBeatsVirtual() =>
        Assert.True(Classify("10.0.0.2", "2002::1", InterfaceKind.Virtual).Tunneled);

    [Fact]
    public void Rule4_Nat64() =>
        Assert.Equal(TrafficCategory.V6Nat64, Classify("2402:f000:5:1::10", "64:ff9b::6506:f82").Category);

    [Fact]
    public void Rule4_CustomNat64Prefix()
    {
        var opts = Defaults with { Nat64Prefixes = [IPNetwork.Parse("2001:db8:64::/96")] };
        Assert.Equal(TrafficCategory.V6Nat64, Classify("2402:f000:5:1::10", "2001:db8:64::1.2.3.4", options: opts).Category);
        Assert.Equal(TrafficCategory.V6, Classify("2402:f000:5:1::10", "64:ff9b::1.2.3.4", options: opts).Category);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("ff02::fb")]
    [InlineData("fd00:1::1")]
    [InlineData("fc00::1")]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    public void Rule5_LocalScopeIsLocal(string remote)
    {
        string local = IPAddress.Parse(remote).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "192.168.100.111" : "fe80::2";
        Assert.Equal(TrafficCategory.Local, Classify(local, remote).Category);
    }

    [Fact]
    public void Rule5_UlaLocalAddressDoesNotMakeGlobalRemoteLocal() =>
        // The dev machine only has fd00:1::/64 behind NAT66; classification looks at the remote.
        Assert.Equal(TrafficCategory.V6, Classify("fd00:1::e83", "2402:f000:1:400::2").Category);

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.100.1")]
    [InlineData("169.254.1.1")]
    public void Rule6_ExemptRangesAreFree(string remote) =>
        Assert.Equal(TrafficCategory.V4Free, Classify("192.168.100.111", remote).Category);

    [Fact]
    public void Rule6_CampusRangeFromConfig()
    {
        var opts = Defaults with { ExemptV4 = [.. Defaults.ExemptV4, IPNetwork.Parse("202.120.0.0/16")] };
        Assert.Equal(TrafficCategory.V4Free, Classify("192.168.100.111", "202.120.1.1", options: opts).Category);
        Assert.Equal(TrafficCategory.V4Billed, Classify("192.168.100.111", "202.121.1.1", options: opts).Category);
    }

    [Theory]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("101.6.15.130")]
    public void Rule7_OtherIPv4IsBilled(string remote) =>
        Assert.Equal(new TrafficClass(TrafficCategory.V4Billed), Classify("192.168.100.111", remote));

    [Fact]
    public void Rule7_OtherIPv6IsV6() =>
        Assert.Equal(new TrafficClass(TrafficCategory.V6), Classify("2402:f000:5:1::10", "2402:f000:1:400::2"));

    [Fact]
    public void UnknownLocalInterfaceIsTreatedAsPhysical() =>
        Assert.Equal(TrafficCategory.V4Billed, Classify("192.168.100.111", "101.6.15.130", InterfaceKind.Unknown).Category);

    [Fact]
    public void ScopedLinkLocalRemoteIsLocal()
    {
        var remote = IPAddress.Parse("fe80::1%12");
        var e = new NetEvent(1, NetDirection.Send, remote.AddressFamily, IPAddress.Parse("fe80::2"), remote, 1);
        Assert.Equal(TrafficCategory.Local, Classifier.Classify(e, InterfaceKind.Physical, Defaults).Category);
    }
}
