using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace V6Meter.Core.Etw;

public enum NetProto : byte { Tcp, Udp }

public enum NetDirection : byte { Send, Recv }

/// <summary>
/// A send/recv event from Microsoft-Windows-Kernel-Network, decoded from the raw payload.
/// Addresses are kept as raw bytes (network order) to avoid allocations on the hot path.
/// Verified in M0: for both send and recv, saddr/sport is the local end and daddr/dport the remote end.
/// The header PID is usually 4 (System); only <see cref="Pid"/> identifies the owning process.
/// </summary>
public readonly struct KernelNetEvent
{
    public required int EventId { get; init; }
    public required int HeaderPid { get; init; }
    public required uint Pid { get; init; }
    public required uint Size { get; init; }
    public required bool IsV6 { get; init; }
    public required NetProto Proto { get; init; }
    public required NetDirection Direction { get; init; }

    /// <summary>16 bytes for v6; first 4 bytes for v4.</summary>
    public required UInt128 DaddrRaw { get; init; }
    public required UInt128 SaddrRaw { get; init; }

    /// <summary>Ports in host order (the payload stores them in network order).</summary>
    public required ushort Dport { get; init; }
    public required ushort Sport { get; init; }

    /// <summary>When the event happened (ETW header timestamp, QPC ticks); 0 when unknown.</summary>
    public long TimestampQpc { get; init; }

    public IPAddress Daddr => ToAddress(DaddrRaw, IsV6);
    public IPAddress Saddr => ToAddress(SaddrRaw, IsV6);

    public static IPAddress ToAddress(UInt128 raw, bool isV6)
    {
        Span<byte> b = stackalloc byte[16];
        MemoryMarshal.Write(b, in raw);
        return isV6 ? new IPAddress(b) : new IPAddress(b[..4]);
    }

    /// <summary>Maps a Kernel-Network event id to its shape; false for events we ignore.</summary>
    public static bool TryDescribe(int id, out bool isV6, out NetProto proto, out NetDirection dir)
    {
        (bool ok, isV6, proto, dir) = id switch
        {
            10 => (true, false, NetProto.Tcp, NetDirection.Send),
            11 => (true, false, NetProto.Tcp, NetDirection.Recv),
            26 => (true, true, NetProto.Tcp, NetDirection.Send),
            27 => (true, true, NetProto.Tcp, NetDirection.Recv),
            42 => (true, false, NetProto.Udp, NetDirection.Send),
            43 => (true, false, NetProto.Udp, NetDirection.Recv),
            58 => (true, true, NetProto.Udp, NetDirection.Send),
            59 => (true, true, NetProto.Udp, NetDirection.Recv),
            _ => (false, false, default(NetProto), default(NetDirection)),
        };
        return ok;
    }

    /// <summary>
    /// Layout (manifest): PID u32, size u32, daddr, saddr, dport u16, sport u16, ...
    /// v4 addresses are 4 bytes, v6 addresses 16 bytes.
    /// </summary>
    public static bool TryParse(int id, int headerPid, ReadOnlySpan<byte> payload, out KernelNetEvent e, long timestampQpc = 0)
    {
        e = default;
        if (!TryDescribe(id, out bool isV6, out var proto, out var dir))
            return false;

        int addrLen = isV6 ? 16 : 4;
        int portOff = 8 + 2 * addrLen;
        if (payload.Length < portOff + 4)
            return false;

        Span<byte> d = stackalloc byte[16], s = stackalloc byte[16];
        d.Clear(); s.Clear();
        payload.Slice(8, addrLen).CopyTo(d);
        payload.Slice(8 + addrLen, addrLen).CopyTo(s);

        e = new KernelNetEvent
        {
            EventId = id,
            HeaderPid = headerPid,
            Pid = BinaryPrimitives.ReadUInt32LittleEndian(payload),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            IsV6 = isV6,
            Proto = proto,
            Direction = dir,
            DaddrRaw = MemoryMarshal.Read<UInt128>(d),
            SaddrRaw = MemoryMarshal.Read<UInt128>(s),
            Dport = BinaryPrimitives.ReadUInt16BigEndian(payload[portOff..]),
            Sport = BinaryPrimitives.ReadUInt16BigEndian(payload[(portOff + 2)..]),
            TimestampQpc = timestampQpc,
        };
        return true;
    }
}
