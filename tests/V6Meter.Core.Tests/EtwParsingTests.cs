using System.Buffers.Binary;
using System.Net;
using System.Text;
using V6Meter.Core.Etw;

namespace V6Meter.Core.Tests;

public class EtwParsingTests
{
    // Builds a Kernel-Network payload the way the provider writes it: ports in network order.
    private static byte[] NetPayload(uint pid, uint size, IPAddress daddr, IPAddress saddr, ushort dport, ushort sport)
    {
        var d = daddr.GetAddressBytes();
        var s = saddr.GetAddressBytes();
        var buf = new byte[8 + d.Length + s.Length + 4 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, pid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), size);
        d.CopyTo(buf, 8);
        s.CopyTo(buf, 8 + d.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8 + d.Length + s.Length), dport);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8 + d.Length + s.Length + 2), sport);
        return buf;
    }

    [Fact]
    public void ParsesTcpV4Recv()
    {
        var p = NetPayload(33268, 1440, IPAddress.Parse("101.6.15.130"), IPAddress.Parse("192.168.100.111"), 443, 59319);
        Assert.True(KernelNetEvent.TryParse(11, 4, p, out var e));
        Assert.Equal(33268u, e.Pid);
        Assert.Equal(4, e.HeaderPid);
        Assert.Equal(1440u, e.Size);
        Assert.False(e.IsV6);
        Assert.Equal(NetProto.Tcp, e.Proto);
        Assert.Equal(NetDirection.Recv, e.Direction);
        Assert.Equal(IPAddress.Parse("101.6.15.130"), e.Daddr);
        Assert.Equal(IPAddress.Parse("192.168.100.111"), e.Saddr);
        Assert.Equal(443, e.Dport);
        Assert.Equal(59319, e.Sport);
    }

    [Fact]
    public void ParsesUdpV6Send()
    {
        var p = NetPayload(7, 1200, IPAddress.Parse("2402:f000:1:400::2"), IPAddress.Parse("fd00:1::e83"), 443, 50000);
        Assert.True(KernelNetEvent.TryParse(58, 7, p, out var e));
        Assert.True(e.IsV6);
        Assert.Equal(NetProto.Udp, e.Proto);
        Assert.Equal(NetDirection.Send, e.Direction);
        Assert.Equal(IPAddress.Parse("2402:f000:1:400::2"), e.Daddr);
        Assert.Equal(IPAddress.Parse("fd00:1::e83"), e.Saddr);
        Assert.Equal(443, e.Dport);
    }

    [Theory]
    [InlineData(10, false, NetProto.Tcp, NetDirection.Send)]
    [InlineData(11, false, NetProto.Tcp, NetDirection.Recv)]
    [InlineData(26, true, NetProto.Tcp, NetDirection.Send)]
    [InlineData(27, true, NetProto.Tcp, NetDirection.Recv)]
    [InlineData(42, false, NetProto.Udp, NetDirection.Send)]
    [InlineData(43, false, NetProto.Udp, NetDirection.Recv)]
    [InlineData(58, true, NetProto.Udp, NetDirection.Send)]
    [InlineData(59, true, NetProto.Udp, NetDirection.Recv)]
    public void EventIdMapping(int id, bool v6, NetProto proto, NetDirection dir)
    {
        Assert.True(KernelNetEvent.TryDescribe(id, out var isV6, out var p, out var d));
        Assert.Equal((v6, proto, dir), (isV6, p, d));
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(28)] [InlineData(49)]
    public void IgnoresOtherEvents(int id) =>
        Assert.False(KernelNetEvent.TryParse(id, 0, new byte[64], out _));

    [Fact]
    public void RejectsTruncatedPayload() =>
        Assert.False(KernelNetEvent.TryParse(27, 0, new byte[40], out _));

    private static byte[] ProcessStartV4(uint pid, DateTime createUtc, byte subAuthorityCount, string image)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(pid);
        w.Write(123UL);                       // ProcessSequenceNumber
        w.Write(createUtc.ToFileTimeUtc());   // CreateTime
        w.Write(1000u);                       // ParentProcessID
        w.Write(122UL);                       // ParentProcessSequenceNumber
        w.Write(1u);                          // SessionID
        w.Write(0u);                          // Flags
        w.Write(3u);                          // ProcessTokenElevationType
        w.Write(1u);                          // ProcessTokenIsElevated
        w.Write((byte)1); w.Write(subAuthorityCount); w.Write(new byte[6]); // SID header
        for (int i = 0; i < subAuthorityCount; i++) w.Write((uint)(0x3000 + i));
        w.Write(Encoding.Unicode.GetBytes(image + "\0"));
        w.Write(0xdeadbeefu);                 // ImageChecksum
        w.Write(0u);                          // TimeDateStamp
        w.Write(Encoding.Unicode.GetBytes("\0\0"));
        w.Write(0u);                          // SecurityMitigations
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ParsesProcessStartV3V4(byte subAuthorities)
    {
        var created = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var p = ProcessStartV4(4242, created, subAuthorities, @"\Device\HarddiskVolume3\Windows\System32\curl.exe");
        foreach (int version in new[] { 3, 4 })
        {
            Assert.True(ProcessStartEvent.TryParse(version, p, out var e));
            Assert.Equal(4242u, e.Pid);
            Assert.Equal(created, e.CreateTimeUtc);
            Assert.Equal(@"\Device\HarddiskVolume3\Windows\System32\curl.exe", e.ImageName);
        }
    }

    [Fact]
    public void ParsesProcessStartV0()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(99u); w.Write(created.ToFileTimeUtc()); w.Write(1u); w.Write(1u);
        w.Write(Encoding.Unicode.GetBytes(@"\Device\HarddiskVolume1\a.exe" + "\0"));
        Assert.True(ProcessStartEvent.TryParse(0, ms.ToArray(), out var e));
        Assert.Equal(new ProcessStartEvent(99, created, @"\Device\HarddiskVolume1\a.exe"), e);
    }

    [Fact]
    public void ProcessStartRejectsTruncated() =>
        Assert.False(ProcessStartEvent.TryParse(4, new byte[30], out _));
}
