using System.Net;
using System.Net.Sockets;
using V6Meter.Core.Classification;

namespace V6Meter.Core.Tests;

public class IpRangesTests
{
    [Fact]
    public void NormalizeMapsIPv4MappedToIPv4()
    {
        var n = IpRanges.Normalize(IPAddress.Parse("::ffff:101.6.15.130"));
        Assert.Equal(AddressFamily.InterNetwork, n.AddressFamily);
        Assert.Equal(IPAddress.Parse("101.6.15.130"), n);
    }

    [Fact]
    public void NormalizeDropsScopeId()
    {
        var n = IpRanges.Normalize(IPAddress.Parse("fe80::1%7"));
        Assert.Equal(0, n.ScopeId);
        Assert.Equal(IPAddress.Parse("fe80::1"), n);
    }

    [Fact]
    public void NormalizeLeavesOthersAlone()
    {
        Assert.Equal(IPAddress.Parse("2402:f000::1"), IpRanges.Normalize(IPAddress.Parse("2402:f000::1")));
        Assert.Equal(IPAddress.Parse("1.2.3.4"), IpRanges.Normalize(IPAddress.Parse("1.2.3.4")));
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("10.0.0.0/8", "11.0.0.0", false)]
    [InlineData("172.16.0.0/12", "172.31.0.1", true)]
    [InlineData("172.16.0.0/12", "172.32.0.1", false)]
    [InlineData("2001::/32", "2001:0:ffff::1", true)]
    [InlineData("2001::/32", "2001:1::1", false)]
    [InlineData("2001::/32", "2001:da8::1", false)]
    [InlineData("64:ff9b::/96", "64:ff9b::101.6.15.130", true)]
    [InlineData("10.0.0.0/8", "::ffff:10.0.0.1", true)] // .NET's IPNetwork.Contains already unmaps IPv4-mapped addresses
    [InlineData("10.0.0.0/8", "2001:db8::a00:1", false)]
    public void CidrMatching(string cidr, string addr, bool expected) =>
        Assert.Equal(expected, IPNetwork.Parse(cidr).Contains(IPAddress.Parse(addr)));

    [Fact]
    public void InAnyChecksEveryNetwork()
    {
        IPNetwork[] nets = [IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("192.168.0.0/16")];
        Assert.True(IpRanges.InAny(IPAddress.Parse("192.168.3.4"), nets));
        Assert.False(IpRanges.InAny(IPAddress.Parse("8.8.8.8"), nets));
        Assert.False(IpRanges.InAny(IPAddress.Parse("8.8.8.8"), []));
    }
}
