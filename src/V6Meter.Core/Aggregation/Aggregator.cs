using V6Meter.Core.Classification;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Model;
using V6Meter.Core.Processes;

namespace V6Meter.Core.Aggregation;

public sealed class ProcessStats(ProcessIdentity identity)
{
    public ProcessIdentity Identity { get; internal set; } = identity;
    public TrafficCounters Total { get; } = new();
    public TrafficCounters LastSecond { get; internal set; } = new();
}

/// <summary>Per-interface ETW-attributed bytes for one second.</summary>
public readonly record struct InterfaceBytes(long In, long Out)
{
    public static InterfaceBytes operator +(InterfaceBytes a, InterfaceBytes b) => new(a.In + b.In, a.Out + b.Out);
}

public sealed record SecondResult(
    TrafficCounters Traffic,
    IReadOnlyDictionary<int, InterfaceBytes> EtwByInterface,
    int FlowsWithUnknownLocal);

/// <summary>Turns one second of flows into category and per-process totals. Single-threaded.</summary>
public sealed class Aggregator(ClassifierOptions options)
{
    private readonly Dictionary<string, ProcessStats> _processes = new();

    public ClassifierOptions Options { get; set; } = options;
    public TrafficCounters Total { get; } = new();
    public IReadOnlyCollection<ProcessStats> Processes => _processes.Values;

    /// <param name="atSecond">QPC second the flows happened in; lets the resolver pick the right owner of a reused PID.</param>
    public SecondResult AddSecond(IEnumerable<KeyValuePair<FlowKey, long>> flows, InterfaceMap map, IProcessResolver resolver, long atSecond = long.MaxValue)
    {
        foreach (var p in _processes.Values)
            p.LastSecond = new TrafficCounters();

        var second = new TrafficCounters();
        var byIf = new Dictionary<int, InterfaceBytes>();
        int unknownLocal = 0;

        foreach (var (key, bytes) in flows)
        {
            var e = NetEventFactory.FromFlow(key, bytes, map.IsLocal);
            var iface = map.Find(e.LocalAddr);
            if (iface == null)
                unknownLocal++;

            var c = Classifier.Classify(e, iface?.Kind ?? InterfaceKind.Unknown, Options);
            if (c.Category == TrafficCategory.Loopback)
                continue;

            second.Add(c, e.Direction, bytes);

            var id = resolver.Resolve(e.Pid, atSecond);
            if (!_processes.TryGetValue(id.Key, out var ps))
                _processes[id.Key] = ps = new ProcessStats(id);
            ps.Identity = id;
            ps.LastSecond.Add(c, e.Direction, bytes);
            ps.Total.Add(c, e.Direction, bytes);

            if (iface != null)
            {
                byIf.TryGetValue(iface.Index, out var ib);
                byIf[iface.Index] = e.Direction == NetDirection.Recv ? ib with { In = ib.In + bytes } : ib with { Out = ib.Out + bytes };
            }
        }

        Total.Add(second);
        return new SecondResult(second, byIf, unknownLocal);
    }
}
