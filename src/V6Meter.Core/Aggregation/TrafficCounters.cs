using V6Meter.Core.Etw;
using V6Meter.Core.Model;

namespace V6Meter.Core.Aggregation;

/// <summary>Bytes per (category, direction), plus how much of V4Billed was tunneled IPv6.</summary>
public sealed class TrafficCounters
{
    private static readonly int Categories = Enum.GetValues<TrafficCategory>().Length;
    private readonly long[] _bytes = new long[Categories * 2];
    private readonly long[] _tunneled = new long[2];

    public long this[TrafficCategory c, NetDirection d] => _bytes[(int)c * 2 + (int)d];

    public long Tunneled(NetDirection d) => _tunneled[(int)d];

    public void Add(TrafficClass c, NetDirection d, long bytes)
    {
        _bytes[(int)c.Category * 2 + (int)d] += bytes;
        if (c.Tunneled)
            _tunneled[(int)d] += bytes;
    }

    public void Add(TrafficCategory c, NetDirection d, long bytes) => _bytes[(int)c * 2 + (int)d] += bytes;

    public void Add(TrafficCounters other)
    {
        for (int i = 0; i < _bytes.Length; i++) _bytes[i] += other._bytes[i];
        for (int i = 0; i < 2; i++) _tunneled[i] += other._tunneled[i];
    }

    /// <summary>"v4" in the main numbers: billed + free (tunneled IPv6 is inside billed).</summary>
    public long V4(NetDirection d) => this[TrafficCategory.V4Billed, d] + this[TrafficCategory.V4Free, d];

    /// <summary>"v6" in the main numbers: native IPv6 only; NAT64 is shown separately.</summary>
    public long V6(NetDirection d) => this[TrafficCategory.V6, d];

    public TrafficCounters Clone()
    {
        var c = new TrafficCounters();
        c.Add(this);
        return c;
    }
}
