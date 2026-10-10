using V6Meter.Core.Estimation;

namespace V6Meter.Core.Tests;

public class FamilySplitEstimatorTests
{
    private static IpFamilyCounters C(uint inRecv, uint inDeliv, uint outReq) => new(inRecv, inDeliv, outReq);

    [Fact]
    public void FirstSampleOnlyEstablishesBaseline() =>
        Assert.Equal(default, new FamilySplitEstimator().Update(C(100, 100, 100), C(5, 5, 5), 1_000_000, 1000));

    [Fact]
    public void SplitsDownloadByReceivedDatagrams()
    {
        var e = new FamilySplitEstimator();
        e.Update(C(0, 0, 0), C(0, 0, 0), 0, 0);
        // Numbers from the dev machine: a v4 download plus background v6.
        var s = e.Update(C(3361, 3417, 3349), C(13, 13, 22), 10_907_554, 200_000);
        Assert.Equal(10_907_554, s.V4In + s.V6In);
        Assert.InRange(s.V4In / (double)(s.V4In + s.V6In), 0.995, 1.0);
    }

    [Fact]
    public void LoopbackDoesNotSkewTheSplit()
    {
        var e = new FamilySplitEstimator();
        e.Update(C(0, 0, 0), C(0, 0, 0), 0, 0);
        // A v6 download while a local proxy pushes 266k loopback datagrams over IPv4
        // (loopback shows up in InDelivers/OutRequests, not in InReceives).
        var s = e.Update(C(19, 266_533, 266_531), C(3075, 3075, 2869), 13_000_000, 400_000);
        Assert.True(s.V6In > 12_900_000, $"v6 in = {s.V6In}");
        Assert.True(s.V6Out > 395_000, $"v6 out = {s.V6Out}");
    }

    [Fact]
    public void UploadRemovesLoopbackFromOutRequests()
    {
        var e = new FamilySplitEstimator();
        e.Update(C(0, 0, 0), C(0, 0, 0), 0, 0);
        // v4: 100 real sends + 900 loopback (900 extra delivers); v6: 100 real sends.
        var s = e.Update(C(50, 950, 1000), C(50, 50, 100), 0, 2000);
        Assert.Equal(1000, s.V4Out);
        Assert.Equal(1000, s.V6Out);
    }

    [Fact]
    public void CounterWrapIsHandled()
    {
        var e = new FamilySplitEstimator();
        e.Update(C(uint.MaxValue - 9, uint.MaxValue - 9, 0), C(0, 0, 0), 0, 0);
        var s = e.Update(C(10, 10, 0), C(20, 20, 0), 4000, 0); // v4 advanced by 20 across the wrap
        Assert.Equal(2000, s.V4In);
        Assert.Equal(2000, s.V6In);
    }

    [Fact]
    public void NoDatagramsOrNoBytesGivesZero()
    {
        var e = new FamilySplitEstimator();
        e.Update(C(0, 0, 0), C(0, 0, 0), 0, 0);
        Assert.Equal(default, e.Update(C(0, 0, 0), C(0, 0, 0), 5000, 5000)); // non-IP traffic (ARP etc.)
        Assert.Equal(default, e.Update(C(10, 10, 10), C(10, 10, 10), 0, 0));
    }
}
