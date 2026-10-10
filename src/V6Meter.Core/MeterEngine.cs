using System.Diagnostics;
using System.Net.NetworkInformation;
using V6Meter.Core.Aggregation;
using V6Meter.Core.Classification;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Processes;

namespace V6Meter.Core;

/// <summary>Immutable copy of one process's counters.</summary>
public sealed record ProcessRow(ProcessIdentity Identity, TrafficCounters LastSecond, TrafficCounters Total);

/// <summary>Admin: ETW, per-process, classified. User: no admin, NIC totals split by an estimate, no processes.</summary>
public enum MeterMode { Admin, User }

/// <summary>A source of once-per-second snapshots.</summary>
public interface IMeterEngine : IDisposable
{
    event Action<MeterSnapshot>? Tick;
    MeterMode Mode { get; }
    InterfaceMap Interfaces { get; }
    void Start();
    void UpdateOptions(ClassifierOptions options);
    void NotifyResumed();
}

/// <summary>What consumers (CLI, tray app) get once per second. Safe to hand to another thread.</summary>
public sealed record MeterSnapshot(
    DateTime Time,
    TrafficCounters LastSecond,
    TrafficCounters Total,
    IReadOnlyList<ProcessRow> Processes,
    ReconcileBytes Reconcile,
    ReconcileBytes ReconcileTotal,
    int EventsLost,
    long EventsTotal,
    InterfaceMap Interfaces,
    MeterMode Mode = MeterMode.Admin,
    bool CollectorDown = false);

/// <summary>
/// Owns the ETW session and the once-per-second pipeline: close the second whose events have all arrived
/// (by event timestamp) → classify/aggregate → reconcile with the NIC counters of that same second →
/// publish snapshot. Figures therefore trail real time by about three seconds.
/// </summary>
public sealed class MeterEngine : IMeterEngine
{
    private static readonly TimeSpan UnknownRebuildInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionRestartInterval = TimeSpan.FromSeconds(5);

    private readonly FlowAccumulator _flows = new();
    private readonly ProcessResolver _resolver = new();
    private readonly Aggregator _aggregator;
    private readonly Reconciler _reconciler = new();
    private readonly CancellationTokenSource _cts = new();
    private KernelNetworkSession? _session;
    private InterfaceMap _map = InterfaceMap.Empty;
    private volatile bool _addressesChanged;
    private DateTime _lastRebuild;
    private Task? _loop;
    private DateTime _sessionStartedUtc;
    private int _lostInEndedSessions;

    public MeterEngine(ClassifierOptions options) => _aggregator = new Aggregator(options);

    public event Action<MeterSnapshot>? Tick;

    public InterfaceMap Interfaces => _map;

    public MeterMode Mode => MeterMode.Admin;

    /// <summary>How many times the ETW session had to be recreated after being stopped externally.</summary>
    public int SessionRestarts { get; private set; }

    /// <summary>Applies new classification options (config reload); takes effect from the next second.</summary>
    public void UpdateOptions(ClassifierOptions options) => _aggregator.Options = options;

    /// <summary>Call after resume from sleep: rebuilds the interface map on the next tick.</summary>
    public void NotifyResumed() => _addressesChanged = true;

    /// <summary>Starts ETW collection. Throws <see cref="UnauthorizedAccessException"/> without admin rights.</summary>
    public void Start()
    {
        RebuildMap();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        StartSession();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private void StartSession()
    {
        _session = new KernelNetworkSession(e => _flows.Add(e), _resolver.OnProcessStart);
        _session.Start();
        _sessionStartedUtc = DateTime.UtcNow;
    }

    /// <summary>Recreates the session if something (e.g. "logman stop v6meter-session") stopped it.</summary>
    private void EnsureSession()
    {
        // A null session means the last restart failed; keep retrying at the same interval.
        if (_session is { HasStopped: false } || DateTime.UtcNow - _sessionStartedUtc < SessionRestartInterval)
            return;
        if (_session != null)
        {
            _lostInEndedSessions += _session.EventsLost;
            _session.Dispose();
            _session = null;
        }
        _sessionStartedUtc = DateTime.UtcNow; // also the time of the next attempt if this one fails
        try
        {
            StartSession();
            SessionRestarts++;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"v6meter session restart failed: {ex}");
        }
    }

    /// <summary>True while no ETW session is collecting (stopped externally and not yet recreated).</summary>
    private bool CollectorDown => _session is null or { HasStopped: true };

    private void OnAddressChanged(object? sender, EventArgs e) => _addressesChanged = true;

    private void RebuildMap()
    {
        _map = InterfaceMapBuilder.Build();
        _lastRebuild = DateTime.UtcNow;
    }

    /// <summary>
    /// A second is closed this long after it ends. Measured with --clock: events reach the callback 1.0–2.9 s
    /// after they happen (p99 2.1 s); the rare later ones are counted in the next second.
    /// </summary>
    private const int CloseDelaySeconds = 2;

    /// <summary>After a long gap (sleep, stalled thread) older seconds are merged instead of replayed one by one.</summary>
    private const int MaxCatchUpSeconds = 10;

    private long _lastClosed;
    private readonly Dictionary<long, Dictionary<int, NicCounters>> _nicAtSecondStart = new();
    private async Task RunAsync(CancellationToken ct)
    {
        _lastClosed = FlowAccumulator.CurrentSecond - CloseDelaySeconds - 2;
        while (!ct.IsCancellationRequested)
        {
            // Tick just after each second boundary, so a NIC sample marks the start of that second.
            long ticks = Stopwatch.GetTimestamp();
            long untilBoundary = Stopwatch.Frequency - ticks % Stopwatch.Frequency;
            await Task.Delay(TimeSpan.FromSeconds((double)untilBoundary / Stopwatch.Frequency + 0.05), ct).ConfigureAwait(false);
            try { TickOnce(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the pipeline alive; a bad second must not stop monitoring.
                System.Diagnostics.Trace.TraceError($"v6meter tick failed: {ex}");
            }
        }
    }

    private void TickOnce()
    {
        EnsureSession();
        // A degraded map (CIM query failed, adapter kinds guessed) is retried like an address change.
        if (_addressesChanged || (_map.Degraded && DateTime.UtcNow - _lastRebuild > UnknownRebuildInterval))
        {
            _addressesChanged = false;
            RebuildMap();
        }

        var map = _map;
        long now = FlowAccumulator.CurrentSecond;
        var nicsNow = new Dictionary<int, NicCounters>();
        foreach (var nic in map.AddressedPhysical)
            if (IfCounters.TryRead(nic.Index, out var inOct, out var outOct))
                nicsNow[nic.Index] = new NicCounters(inOct, outOct);
        _nicAtSecondStart[now] = nicsNow;

        // Close every second whose events have had time to arrive, in order.
        long target = now - 1 - CloseDelaySeconds;
        if (target <= _lastClosed)
            return; // woke up early
        if (target - _lastClosed > MaxCatchUpSeconds)
            _lastClosed = target - MaxCatchUpSeconds; // Take() merges the skipped seconds into the first one

        SecondResult second = null!;
        ReconcileBytes rec = default;
        for (long s = _lastClosed + 1; s <= target; s++)
        {
            second = _aggregator.AddSecond(_flows.Take(s), map, _resolver, s);
            // NIC delta: counters at the end of s (= start of s + 1) vs the previous sample. Without that sample
            // the next delta spans this second too, so its ETW bytes are carried over to match.
            if (_nicAtSecondStart.TryGetValue(s + 1, out var nics))
            {
                rec = _reconciler.Update(nics, second.EtwByInterface);
            }
            else
            {
                _reconciler.Defer(second.EtwByInterface);
                rec = default;
            }
            _lastClosed = s;

            // Unknown local addresses usually mean a new IPv6 temporary address the event missed.
            if (second.FlowsWithUnknownLocal > 0 && DateTime.UtcNow - _lastRebuild > UnknownRebuildInterval)
                _addressesChanged = true;
        }
        foreach (var old in _nicAtSecondStart.Keys.Where(k => k <= target).ToList())
            _nicAtSecondStart.Remove(old);

        Tick?.Invoke(new MeterSnapshot(
            DateTime.Now,
            second.Traffic,
            _aggregator.Total.Clone(),
            _aggregator.Processes.Select(p => new ProcessRow(p.Identity, p.LastSecond.Clone(), p.Total.Clone())).ToList(),
            rec,
            _reconciler.Total,
            _lostInEndedSessions + (_session?.EventsLost ?? 0),
            _flows.TotalEvents,
            map,
            CollectorDown: CollectorDown));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _session?.Dispose();
        _cts.Dispose();
    }
}
