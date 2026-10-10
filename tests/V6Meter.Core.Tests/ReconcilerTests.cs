using V6Meter.Core.Aggregation;

namespace V6Meter.Core.Tests;

public class ReconcilerTests
{
    private static Dictionary<int, NicCounters> Nics(params (int Index, ulong In, ulong Out)[] n) =>
        n.ToDictionary(x => x.Index, x => new NicCounters(x.In, x.Out));

    private static Dictionary<int, InterfaceBytes> Etw(params (int Index, long In, long Out)[] e) =>
        e.ToDictionary(x => x.Index, x => new InterfaceBytes(x.In, x.Out));

    [Fact]
    public void FirstSampleOnlyEstablishesBaseline()
    {
        var r = new Reconciler();
        var s = r.Update(Nics((7, 1_000_000, 500)), Etw((7, 999, 9)));
        Assert.Equal(default, s);
        Assert.Equal(default, r.Total);
    }

    [Fact]
    public void UnattributedIsNicDeltaMinusEtw()
    {
        var r = new Reconciler();
        r.Update(Nics((7, 1000, 100)), Etw());
        var s = r.Update(Nics((7, 11000, 600)), Etw((7, 9500, 450)));
        Assert.Equal(new ReconcileBytes(10000, 500, 9500, 450), s);
        Assert.Equal(500, s.UnattributedIn);
        Assert.Equal(50, s.UnattributedOut);
    }

    [Fact]
    public void MissingSampleCarriesEtwBytesIntoTheNextDelta()
    {
        // Seconds 1 and 2 closed, but only one NIC sample (at the end of 2) exists: the delta spans both.
        var r = new Reconciler();
        r.Update(Nics((7, 0, 0)), Etw());
        r.Defer(Etw((7, 1000, 10)));
        var s = r.Update(Nics((7, 2100, 25)), Etw((7, 1000, 10)));
        Assert.Equal(new ReconcileBytes(2100, 25, 2000, 20), s);
        Assert.Equal(100, r.Total.NicIn - r.Total.EtwIn);

        // Deferred bytes are used once.
        Assert.Equal(new ReconcileBytes(100, 0, 100, 0), r.Update(Nics((7, 2200, 25)), Etw((7, 100, 0))));
    }

    [Fact]
    public void UnattributedPerSecondIsClampedButTotalsAreNot()
    {
        var r = new Reconciler();
        r.Update(Nics((7, 0, 0)), Etw());
        // ETW lags ~1 s, so a second can attribute more than the NIC counted.
        var s1 = r.Update(Nics((7, 1000, 0)), Etw((7, 1500, 0)));
        Assert.Equal(0, s1.UnattributedIn);
        r.Update(Nics((7, 3000, 0)), Etw((7, 1300, 0)));
        Assert.Equal(new ReconcileBytes(3000, 0, 2800, 0), r.Total);
    }

    [Fact]
    public void CounterResetDropsThatSecondForThatNic()
    {
        var r = new Reconciler();
        r.Update(Nics((7, 5000, 5000), (12, 100, 100)), Etw());
        var s = r.Update(Nics((7, 200, 300), (12, 300, 150)), Etw((7, 999, 999), (12, 150, 40)));
        Assert.Equal(new ReconcileBytes(200, 50, 150, 40), s); // only NIC 12 counted
        var s2 = r.Update(Nics((7, 1200, 400), (12, 300, 150)), Etw((7, 900, 90)));
        Assert.Equal(new ReconcileBytes(1000, 100, 900, 90), s2); // NIC 7 resumes from the new baseline
    }

    [Fact]
    public void ResetInOnlyOneDirectionStillDropsTheNic()
    {
        var r = new Reconciler();
        r.Update(Nics((7, 5000, 5000)), Etw());
        Assert.Equal(default, r.Update(Nics((7, 6000, 10)), Etw((7, 1000, 0))));
    }

    [Fact]
    public void NicThatDisappearsAndReturnsStartsFromNewBaseline()
    {
        var r = new Reconciler();
        r.Update(Nics((7, 1000, 0)), Etw());
        r.Update(Nics(), Etw());
        Assert.Equal(default, r.Update(Nics((7, 999_999, 0)), Etw()));
        Assert.Equal(new ReconcileBytes(1, 0, 0, 0), r.Update(Nics((7, 1_000_000, 0)), Etw()));
    }

    [Fact]
    public void EtwOnNonPhysicalInterfacesIsIgnored()
    {
        var r = new Reconciler();
        r.Update(Nics((7, 0, 0)), Etw());
        var s = r.Update(Nics((7, 1000, 0)), Etw((7, 800, 0), (25, 5000, 0)));
        Assert.Equal(800, s.EtwIn);
    }
}
