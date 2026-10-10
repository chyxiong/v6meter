using System.Diagnostics;
using System.Runtime.InteropServices;
using V6Meter.Core.Etw;

namespace V6Meter.Core.Aggregation;

/// <summary>One (process, direction, local, remote) tuple, with addresses as raw ETW bytes.</summary>
public readonly record struct FlowKey(uint Pid, NetDirection Direction, bool IsV6, UInt128 Saddr, UInt128 Daddr);

/// <summary>
/// The only thing the ETW callback touches: sums bytes per flow, in one bucket per second of the event's own
/// timestamp. Real-time ETW delivers events in batches 1–3 s late, so bucketing by arrival time made some
/// seconds empty and the next one double; by timestamp every second gets its own traffic. The aggregation
/// thread closes a second with <see cref="Take"/> once its events have arrived; anything arriving later
/// for a closed second goes into the next open one, so no bytes are lost.
/// </summary>
public sealed class FlowAccumulator
{
    private readonly object _lock = new();
    private readonly Dictionary<long, Dictionary<FlowKey, long>> _buckets = new();
    private readonly Stack<Dictionary<FlowKey, long>> _free = new();
    private Dictionary<FlowKey, long>? _handedOut;
    private long _closedThrough = long.MinValue;
    private long _cachedSecond = long.MinValue;
    private Dictionary<FlowKey, long>? _cached;
    private long _events;

    public long TotalEvents => Interlocked.Read(ref _events);

    /// <summary>Whole seconds on the QPC clock, which ETW real-time timestamps use (verified with --clock).</summary>
    public static long SecondOf(long qpcTicks) => qpcTicks / Stopwatch.Frequency;

    public static long CurrentSecond => SecondOf(Stopwatch.GetTimestamp());

    public void Add(in KernelNetEvent e)
    {
        if (e.Size == 0)
            return;
        long second = SecondOf(e.TimestampQpc != 0 ? e.TimestampQpc : Stopwatch.GetTimestamp());
        Add(second, new FlowKey(e.Pid, e.Direction, e.IsV6, e.SaddrRaw, e.DaddrRaw), e.Size);
    }

    internal void Add(long second, FlowKey key, long bytes)
    {
        lock (_lock)
        {
            if (second <= _closedThrough)
                second = _closedThrough + 1; // late for a closed second: count it in the next one
            if (second != _cachedSecond || _cached == null)
            {
                if (!_buckets.TryGetValue(second, out var bucket))
                    _buckets[second] = bucket = Rent();
                (_cachedSecond, _cached) = (second, bucket);
            }
            CollectionsMarshal.GetValueRefOrAddDefault(_cached, key, out _) += bytes;
            _events++;
        }
    }

    /// <summary>
    /// Closes <paramref name="second"/>: returns its flows plus those of any earlier second still pending
    /// (after a gap they are merged rather than dropped). The returned dictionary is reused on the next call.
    /// </summary>
    public Dictionary<FlowKey, long> Take(long second)
    {
        lock (_lock)
        {
            if (_handedOut != null)
                Return(_handedOut);

            Dictionary<FlowKey, long>? result = null;
            foreach (var s in _buckets.Keys.Where(s => s <= second).Order().ToList())
            {
                var bucket = _buckets[s];
                _buckets.Remove(s);
                if (result == null)
                    result = bucket;
                else
                {
                    foreach (var (k, v) in bucket)
                        CollectionsMarshal.GetValueRefOrAddDefault(result, k, out _) += v;
                    Return(bucket);
                }
            }
            result ??= Rent();

            _closedThrough = Math.Max(_closedThrough, second);
            if (_cachedSecond <= _closedThrough)
                (_cachedSecond, _cached) = (long.MinValue, null);
            return _handedOut = result;
        }
    }

    private Dictionary<FlowKey, long> Rent() => _free.TryPop(out var d) ? d : new Dictionary<FlowKey, long>();

    private void Return(Dictionary<FlowKey, long> d)
    {
        d.Clear();
        if (_free.Count < 8)
            _free.Push(d);
    }
}
