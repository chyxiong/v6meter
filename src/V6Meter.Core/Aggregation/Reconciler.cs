namespace V6Meter.Core.Aggregation;

public readonly record struct NicCounters(ulong InOctets, ulong OutOctets);

/// <summary>One second of reconciliation, summed over physical NICs. In = download, Out = upload.</summary>
public readonly record struct ReconcileBytes(long NicIn, long NicOut, long EtwIn, long EtwOut)
{
    /// <summary>NIC bytes ETW could not attribute (headers, WSL/VMs, ...); clamped at 0 for display.</summary>
    public long UnattributedIn => Math.Max(0, NicIn - EtwIn);
    public long UnattributedOut => Math.Max(0, NicOut - EtwOut);
}

/// <summary>
/// Compares physical NIC counter deltas with ETW-attributed bytes on the same NIC.
/// A NIC whose counter went backwards (reconnect, resume from sleep) is skipped for that second.
/// Cumulative totals only include seconds that were valid for that NIC.
/// </summary>
public sealed class Reconciler
{
    private readonly Dictionary<int, NicCounters> _last = new();

    /// <summary>Cumulative over valid seconds; <c>NicIn − EtwIn</c> is cumulative unattributed download.</summary>
    public ReconcileBytes Total { get; private set; }

    private readonly Dictionary<int, InterfaceBytes> _deferred = new();

    /// <summary>
    /// ETW bytes of a second whose closing NIC sample is missing (stalled thread). The next NIC delta spans
    /// that second too, so these are added to the next <see cref="Update"/> to keep both sides over the same span.
    /// </summary>
    public void Defer(IReadOnlyDictionary<int, InterfaceBytes> etwByInterface)
    {
        foreach (var (index, b) in etwByInterface)
            _deferred[index] = _deferred.GetValueOrDefault(index) + b;
    }

    public ReconcileBytes Update(IReadOnlyDictionary<int, NicCounters> nics, IReadOnlyDictionary<int, InterfaceBytes> etwByInterface)
    {
        if (_deferred.Count > 0)
        {
            Defer(etwByInterface);
            etwByInterface = new Dictionary<int, InterfaceBytes>(_deferred);
            _deferred.Clear();
        }
        long nicIn = 0, nicOut = 0, etwIn = 0, etwOut = 0;
        foreach (var (index, cur) in nics)
        {
            bool hadLast = _last.TryGetValue(index, out var last);
            _last[index] = cur;
            if (!hadLast || cur.InOctets < last.InOctets || cur.OutOctets < last.OutOctets)
                continue;

            nicIn += (long)(cur.InOctets - last.InOctets);
            nicOut += (long)(cur.OutOctets - last.OutOctets);
            if (etwByInterface.TryGetValue(index, out var etw))
            {
                etwIn += etw.In;
                etwOut += etw.Out;
            }
        }

        foreach (var gone in _last.Keys.Where(k => !nics.ContainsKey(k)).ToList())
            _last.Remove(gone);

        Total = new ReconcileBytes(Total.NicIn + nicIn, Total.NicOut + nicOut, Total.EtwIn + etwIn, Total.EtwOut + etwOut);
        return new ReconcileBytes(nicIn, nicOut, etwIn, etwOut);
    }
}
