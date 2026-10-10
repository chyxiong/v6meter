using System.Net.NetworkInformation;
using V6Meter.Core.Aggregation;
using V6Meter.Core.Classification;
using V6Meter.Core.Estimation;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Model;

namespace V6Meter.Core;

/// <summary>
/// Runs without admin rights: no ETW, so no per-process data and no classification. Physical NIC totals
/// are split into IPv4/IPv6 by <see cref="FamilySplitEstimator"/>. The IPv4 estimate is reported as
/// V4Billed (we cannot tell exempt ranges apart, and over-reporting billed traffic is the safe side).
/// </summary>
public sealed class UserModeEngine : IMeterEngine
{
    private readonly FamilySplitEstimator _estimator = new();
    private readonly Reconciler _nicDeltas = new(); // used only for its per-NIC delta / reset handling
    private readonly TrafficCounters _total = new();
    private readonly CancellationTokenSource _cts = new();
    private InterfaceMap _map = InterfaceMap.Empty;
    private volatile bool _addressesChanged;
    private Task? _loop;

    public event Action<MeterSnapshot>? Tick;

    public MeterMode Mode => MeterMode.User;
    public InterfaceMap Interfaces => _map;

    public void Start()
    {
        RebuildMap();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void UpdateOptions(ClassifierOptions options) { } // nothing is classified in user mode

    public void NotifyResumed() => _addressesChanged = true;

    private void OnAddressChanged(object? sender, EventArgs e) => _addressesChanged = true;

    private DateTime _lastRebuild;

    private void RebuildMap()
    {
        _map = InterfaceMapBuilder.Build();
        _lastRebuild = DateTime.UtcNow;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { TickOnce(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceError($"v6meter user-mode tick failed: {ex}");
            }
        }
    }

    private void TickOnce()
    {
        // A degraded map (adapter query failed, kinds guessed) is retried every few seconds.
        if (_addressesChanged || (_map.Degraded && DateTime.UtcNow - _lastRebuild > TimeSpan.FromSeconds(5)))
        {
            _addressesChanged = false;
            RebuildMap();
        }

        var nics = new Dictionary<int, NicCounters>();
        foreach (var iface in _map.AddressedPhysical)
            if (IfCounters.TryRead(iface.Index, out var i, out var o))
                nics[iface.Index] = new NicCounters(i, o);
        var nic = _nicDeltas.Update(nics, new Dictionary<int, InterfaceBytes>());

        var second = new TrafficCounters();
        if (IpFamilyCounters.TryRead(false, out var v4) && IpFamilyCounters.TryRead(true, out var v6))
        {
            var split = _estimator.Update(v4, v6, nic.NicIn, nic.NicOut);
            second.Add(TrafficCategory.V4Billed, NetDirection.Recv, split.V4In);
            second.Add(TrafficCategory.V4Billed, NetDirection.Send, split.V4Out);
            second.Add(TrafficCategory.V6, NetDirection.Recv, split.V6In);
            second.Add(TrafficCategory.V6, NetDirection.Send, split.V6Out);
        }
        _total.Add(second);

        Tick?.Invoke(new MeterSnapshot(
            DateTime.Now, second, _total.Clone(), [], default, default, 0, 0, _map, MeterMode.User));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
