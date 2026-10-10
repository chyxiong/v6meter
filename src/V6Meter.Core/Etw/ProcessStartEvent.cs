using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace V6Meter.Core.Etw;

/// <summary>Microsoft-Windows-Kernel-Process event 1 (ProcessStart).</summary>
/// <param name="ImageName">NT device path, e.g. \Device\HarddiskVolume3\Windows\System32\curl.exe.</param>
public readonly record struct ProcessStartEvent(uint Pid, DateTime CreateTimeUtc, string ImageName)
{
    public static readonly Guid ProviderGuid = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    public const ulong KeywordProcess = 0x10;
    public const int EventId = 1;

    /// <summary>QPC timestamp of the event header (same clock as Stopwatch); 0 if unknown.</summary>
    public long TimestampQpc { get; init; }

    /// <summary>Parses versions 0–4 of the ProcessStart template (layouts from the provider manifest).</summary>
    public static bool TryParse(int version, ReadOnlySpan<byte> p, out ProcessStartEvent e)
    {
        e = default;
        int createOff, nameOff;
        switch (version)
        {
            case 0: createOff = 4; nameOff = 20; break;               // PID, CreateTime, ParentPID, SessionID, ImageName
            case 1 or 2: createOff = 4; nameOff = 24; break;          // ... + Flags
            case >= 3:
                // PID u32, ProcessSequenceNumber u64, CreateTime, ParentPID u32, ParentSeq u64, SessionID, Flags,
                // ElevationType, IsElevated, MandatoryLabel (SID, variable), ImageName, ...
                createOff = 12;
                const int sidOff = 48;
                if (p.Length < sidOff + 8) return false;
                nameOff = sidOff + 8 + 4 * p[sidOff + 1];
                break;
            default: return false;
        }
        if (p.Length < nameOff + 2)
            return false;

        var name = MemoryMarshal.Cast<byte, char>(p[nameOff..(p.Length & ~1)]);
        int end = name.IndexOf('\0');
        if (end < 0) end = name.Length;

        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(p[createOff..]);
        e = new ProcessStartEvent(
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue,
            new string(name[..end]));
        return true;
    }
}
