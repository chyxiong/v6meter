using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using V6Meter.Core.Interfaces;

namespace V6Meter.Core.Tests;

public class InterfaceTests
{
    private static (InterfaceKind, bool) Decide(string name, string desc, NetworkInterfaceType type, bool? connector, bool gw = false) =>
        InterfaceKindRules.Decide(new AdapterFacts(name, desc, type, connector, gw));

    [Fact]
    public void PhysicalWhenConnectorPresent()
    {
        Assert.Equal((InterfaceKind.Physical, false), Decide("以太网", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, true, true));
        Assert.Equal((InterfaceKind.Physical, false), Decide("WLAN", "Intel(R) Wi-Fi 6E AX211 160MHz", NetworkInterfaceType.Wireless80211, true));
    }

    [Theory]
    [InlineData("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter")]
    [InlineData("6to4 Adapter", "Microsoft 6to4 Adapter")]
    [InlineData("Microsoft IP-HTTPS Platform Interface", "Microsoft IP-HTTPS Platform Adapter")]
    public void TunnelAdapters(string name, string desc) =>
        Assert.Equal((InterfaceKind.Tunnel4in6, false), Decide(name, desc, NetworkInterfaceType.Tunnel, null));

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, true, true)]
    [InlineData(NetworkInterfaceType.Wireless80211, true, true)]
    [InlineData(NetworkInterfaceType.Ethernet, false, null)]
    [InlineData(NetworkInterfaceType.Tunnel, true, null)]
    [InlineData(NetworkInterfaceType.Ppp, true, null)]
    public void GuessWhenCimFails(NetworkInterfaceType type, bool gw, bool? expected) =>
        Assert.Equal(expected, InterfaceKindRules.GuessConnectorPresent(type, gw));

    [Fact]
    public void OtherTunnelTypeIsVirtual() =>
        Assert.Equal(InterfaceKind.Virtual, Decide("isatap", "Microsoft ISATAP Adapter", NetworkInterfaceType.Tunnel, null).Item1);

    // Adapters seen on the dev machine: all lack a physical connector.
    [Theory]
    [InlineData("ZeroTier One [0123456789abcdef]", "ZeroTier Virtual Port", true)]
    [InlineData("以太网 2", "Netease UU TAP-Win32 Adapter V9.21", false)]
    [InlineData("本地连接", "TAP-Windows Adapter V9", false)]
    [InlineData("OpenVPN Data Channel Offload for Surfshark", "OpenVPN Data Channel Offload", false)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData("wg0", "WireGuard Tunnel", true)]
    [InlineData("Meta", "Wintun Userspace Tunnel", true)]
    public void VirtualAdapters(string name, string desc, bool hasGateway) =>
        Assert.Equal((InterfaceKind.Virtual, false), Decide(name, desc, NetworkInterfaceType.Ethernet, false, hasGateway));

    [Fact]
    public void HyperVExternalSwitchHostVnicWithGatewayIsPhysicalAndFlagged() =>
        Assert.Equal((InterfaceKind.Physical, true), Decide("vEthernet (External)", "Hyper-V Virtual Ethernet Adapter #2", NetworkInterfaceType.Ethernet, false, true));

    [Fact]
    public void LoopbackAdapter() =>
        Assert.Equal(InterfaceKind.Loopback, Decide("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback, null).Item1);

    [Fact]
    public void MapFindsAddressesIgnoringScopeAndMapping()
    {
        var eth = new InterfaceInfo(7, "以太网", "Realtek", InterfaceKind.Physical);
        var zt = new InterfaceInfo(25, "ZeroTier", "ZeroTier Virtual Port", InterfaceKind.Virtual);
        var map = new InterfaceMap([
            (eth, [IPAddress.Parse("192.168.100.111"), IPAddress.Parse("fe80::6f53:de73:e543:e693%7"), IPAddress.Parse("fd00:1::e83")]),
            (zt, [IPAddress.Parse("192.168.195.91")]),
        ]);

        Assert.Same(eth, map.Find(IPAddress.Parse("::ffff:192.168.100.111")));
        Assert.Same(eth, map.Find(IPAddress.Parse("fe80::6f53:de73:e543:e693")));
        Assert.Same(zt, map.Find(IPAddress.Parse("192.168.195.91")));
        Assert.Null(map.Find(IPAddress.Parse("101.6.15.130")));
        Assert.True(map.IsLocal(IPAddress.Parse("fd00:1::e83")));
    }

    [Fact]
    public void AddressedPhysicalExcludesNicsWithoutAddresses()
    {
        var bound = new InterfaceInfo(3, "以太网", "Intel NIC bound to vSwitch", InterfaceKind.Physical);
        var host = new InterfaceInfo(40, "vEthernet (External)", "Hyper-V Virtual Ethernet Adapter #2", InterfaceKind.Physical, IsHyperVExternal: true);
        var map = new InterfaceMap([(bound, []), (host, [IPAddress.Parse("10.1.2.3")])]);
        Assert.Equal([host], map.AddressedPhysical);
        Assert.True(map.HasHyperVExternal);
    }

    [Fact]
    public void MibIfRow2HasNativeSize() =>
        Assert.Equal(1352, Marshal.SizeOf<IfCounters.MibIfRow2>());
}
