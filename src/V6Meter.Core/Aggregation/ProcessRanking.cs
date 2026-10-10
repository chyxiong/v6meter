using V6Meter.Core.Etw;
using V6Meter.Core.Model;

namespace V6Meter.Core.Aggregation;

/// <summary>
/// Which processes the main window lists: only those that downloaded a meaningful amount this run, and at
/// most a fixed number of them. When there are more, the ones with the smallest total drop out.
/// </summary>
public static class ProcessRanking
{
    /// <summary>Rows below this total (v4 + v6 download this run) are not shown. Also used for the proxy hint.</summary>
    public const long MinTotalBytes = 10L * 1024 * 1024;

    public const int MaxRows = 20;

    /// <summary>The "总流量" column: IPv4 plus IPv6 download since start (all categories of each family).</summary>
    public static long TotalDown(TrafficCounters t) => t.V4(NetDirection.Recv) + t.V6(NetDirection.Recv);

    public static IReadOnlyList<ProcessRow> Visible(IEnumerable<ProcessRow> rows, long minBytes = MinTotalBytes, int maxRows = MaxRows) =>
        rows.Select(p => (p, total: TotalDown(p.Total)))
            .Where(x => x.total >= minBytes)
            .OrderByDescending(x => x.total)
            .ThenBy(x => x.p.Identity.Key, StringComparer.Ordinal) // stable on ties, so rows do not swap in and out
            .Take(Math.Max(0, maxRows))
            .Select(x => x.p)
            .ToList();
}
