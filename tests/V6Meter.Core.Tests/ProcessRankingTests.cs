using V6Meter.Core.Aggregation;
using V6Meter.Core.Etw;
using V6Meter.Core.Model;
using V6Meter.Core.Processes;

namespace V6Meter.Core.Tests;

public class ProcessRankingTests
{
    private const long MiB = 1024 * 1024;

    private static ProcessRow Row(string key, long v4Down, long v6Down = 0, long v4Up = 0)
    {
        var total = new TrafficCounters();
        total.Add(TrafficCategory.V4Billed, NetDirection.Recv, v4Down);
        total.Add(TrafficCategory.V6, NetDirection.Recv, v6Down);
        total.Add(TrafficCategory.V4Billed, NetDirection.Send, v4Up);
        return new ProcessRow(new ProcessIdentity(key, key, null), new TrafficCounters(), total);
    }

    [Fact]
    public void TotalIsV4PlusV6Download()
    {
        var t = Row("a", 3 * MiB, 4 * MiB, v4Up: 100 * MiB).Total;
        t.Add(TrafficCategory.V4Free, NetDirection.Recv, 2 * MiB);
        Assert.Equal(9 * MiB, ProcessRanking.TotalDown(t)); // upload is not counted; free IPv4 is
    }

    [Fact]
    public void RowsUnderTenMegabytesAreHidden()
    {
        var visible = ProcessRanking.Visible([Row("small", 10 * MiB - 1), Row("exact", 10 * MiB), Row("split", 6 * MiB, 5 * MiB)]);
        Assert.Equal(["split", "exact"], visible.Select(r => r.Identity.Key));
    }

    [Fact]
    public void OverTheLimitTheSmallestTotalsDropOut()
    {
        var rows = Enumerable.Range(1, 25).Select(i => Row($"p{i:00}", i * 20 * MiB)).ToList();
        var visible = ProcessRanking.Visible(rows);
        Assert.Equal(ProcessRanking.MaxRows, visible.Count);
        Assert.Equal("p25", visible[0].Identity.Key);
        Assert.DoesNotContain(visible, r => r.Identity.Key is "p01" or "p05");
        Assert.Contains(visible, r => r.Identity.Key == "p06");
    }

    [Fact]
    public void TiesAreBrokenStably()
    {
        var a = ProcessRanking.Visible([Row("b", 50 * MiB), Row("a", 50 * MiB), Row("c", 50 * MiB)], maxRows: 2);
        var b = ProcessRanking.Visible([Row("c", 50 * MiB), Row("a", 50 * MiB), Row("b", 50 * MiB)], maxRows: 2);
        Assert.Equal(["a", "b"], a.Select(r => r.Identity.Key));
        Assert.Equal(a.Select(r => r.Identity.Key), b.Select(r => r.Identity.Key));
    }
}
