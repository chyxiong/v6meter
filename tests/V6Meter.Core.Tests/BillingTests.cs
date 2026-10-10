using V6Meter.Core.Aggregation;
using V6Meter.Core.Alerts;
using V6Meter.Core.Billing;
using V6Meter.Core.Campus;
using V6Meter.Core.Config;
using V6Meter.Core.Etw;
using V6Meter.Core.Formatting;
using V6Meter.Core.Model;
using V6Meter.Core.Processes;

namespace V6Meter.Core.Tests;

public class BillingRulesTests
{
    private static TrafficCounters Counters()
    {
        var t = new TrafficCounters();
        t.Add(TrafficCategory.V4Billed, NetDirection.Recv, 1000);
        t.Add(TrafficCategory.V4Billed, NetDirection.Send, 100);
        t.Add(TrafficCategory.V6Nat64, NetDirection.Recv, 50);
        t.Add(TrafficCategory.V6Nat64, NetDirection.Send, 5);
        t.Add(TrafficCategory.V4Free, NetDirection.Recv, 7777);
        t.Add(TrafficCategory.V6, NetDirection.Recv, 8888);
        return t;
    }

    [Fact]
    public void DefaultCountsBilledDownloadPlusNat64() =>
        Assert.Equal(1050, BillingRules.BilledBytes(Counters(), new AppConfig()));

    [Fact]
    public void Nat64CanBeExcluded() =>
        Assert.Equal(1000, BillingRules.BilledBytes(Counters(), new AppConfig { Nat64IsBilled = false }));

    [Fact]
    public void UploadCountsWhenConfigured() =>
        Assert.Equal(1155, BillingRules.BilledBytes(Counters(), new AppConfig { BillingCountsUpload = true }));
}

public class RateAlerterTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0);
    private const long Above = 2 * 1048576, Below = 1048576 / 2;
    private static readonly (string, string)? Baidu = ("baidu", "百度网盘"), Quark = ("quark", "夸克");

    [Fact]
    public void AlertsAfterSustainedSeconds()
    {
        var a = new RateAlerter(1.0, 3, TimeSpan.FromMinutes(5));
        Assert.Null(a.Update(T0, Above, Baidu));
        Assert.True(a.IsAbove);
        Assert.Null(a.Update(T0.AddSeconds(1), Above, Baidu));
        var alert = a.Update(T0.AddSeconds(2), Above, Baidu);
        Assert.NotNull(alert);
        Assert.Equal("百度网盘", alert.ProcessName);
        Assert.Equal(2.0, alert.MiBPerSec, 3);
    }

    [Fact]
    public void DipBelowResetsTheStreak()
    {
        var a = new RateAlerter(1.0, 3, TimeSpan.FromMinutes(5));
        a.Update(T0, Above, Baidu);
        a.Update(T0.AddSeconds(1), Above, Baidu);
        Assert.Null(a.Update(T0.AddSeconds(2), Below, Baidu));
        Assert.False(a.IsAbove);
        Assert.Null(a.Update(T0.AddSeconds(3), Above, Baidu));
        Assert.Null(a.Update(T0.AddSeconds(4), Above, Baidu));
        Assert.NotNull(a.Update(T0.AddSeconds(5), Above, Baidu));
    }

    [Fact]
    public void SameProcessIsNotRepeatedWithinCooldownButOthersAre()
    {
        var a = new RateAlerter(1.0, 1, TimeSpan.FromMinutes(5));
        Assert.NotNull(a.Update(T0, Above, Baidu));
        Assert.Null(a.Update(T0.AddSeconds(1), Above, Baidu));
        Assert.Null(a.Update(T0.AddMinutes(4), Above, Baidu));
        Assert.NotNull(a.Update(T0.AddMinutes(4).AddSeconds(1), Above, Quark));
        Assert.NotNull(a.Update(T0.AddMinutes(5), Above, Baidu));
    }

    [Fact]
    public void ExactlyAtThresholdIsNotAbove()
    {
        var a = new RateAlerter(1.0, 1, TimeSpan.Zero);
        Assert.Null(a.Update(T0, 1048576, Baidu));
        Assert.False(a.IsAbove);
    }

    [Fact]
    public void ZeroThresholdDisablesAlerts()
    {
        var a = new RateAlerter(0, 1, TimeSpan.Zero);
        Assert.Null(a.Update(T0, long.MaxValue / 2, Baidu));
        Assert.False(a.IsAbove);
    }

    [Fact]
    public void NoTopProcessNoAlertButStillHot()
    {
        var a = new RateAlerter(1.0, 1, TimeSpan.Zero);
        Assert.Null(a.Update(T0, Above, null));
        Assert.True(a.IsAbove);
    }

    [Fact]
    public void SnoozeSilencesAlertsButKeepsTheHighlight()
    {
        var a = new RateAlerter(1.0, 1, TimeSpan.Zero) { SnoozedUntil = RateAlerter.OneHourFrom(T0) };
        Assert.Null(a.Update(T0, Above, Baidu));
        Assert.True(a.IsAbove);
        Assert.Null(a.Update(T0.AddMinutes(59), Above, Quark));
        Assert.True(a.IsSnoozed(T0.AddMinutes(59)));
    }

    [Fact]
    public void AlertResumesWhenSnoozeEndsWhileStillAbove()
    {
        // Snoozed alerts do not start the cooldown, so the first second after the snooze can alert.
        var a = new RateAlerter(1.0, 3, TimeSpan.FromMinutes(5)) { SnoozedUntil = T0.AddSeconds(10) };
        for (int s = 0; s < 10; s++)
            Assert.Null(a.Update(T0.AddSeconds(s), Above, Baidu));
        Assert.NotNull(a.Update(T0.AddSeconds(10), Above, Baidu));
    }

    [Theory]
    [InlineData("2026-10-10 12:00", "2026-10-11 00:00")]
    [InlineData("2026-10-10 00:00", "2026-10-11 00:00")]
    [InlineData("2026-10-10 23:59", "2026-10-11 00:00")]
    [InlineData("2026-12-31 18:30", "2027-01-01 00:00")]
    public void TodayEndsAtMidnight(string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected), RateAlerter.EndOfDay(DateTime.Parse(now)));

    [Fact]
    public void ClearingTheSnoozeResumesAtOnce()
    {
        var a = new RateAlerter(1.0, 1, TimeSpan.Zero) { SnoozedUntil = RateAlerter.EndOfDay(T0) };
        Assert.Null(a.Update(T0, Above, Baidu));
        a.SnoozedUntil = default;
        Assert.NotNull(a.Update(T0.AddSeconds(1), Above, Baidu));
    }
}

public class CampusPortalTests
{
    [Fact]
    public void ParsesStatusPage()
    {
        const string html = """
            <script>
            var time='1234      ';flow='5242880   ';fsele=1;fee='123456';xsele=0;
            </script>
            """;
        var s = CampusPortal.Parse(html);
        Assert.NotNull(s);
        Assert.Equal(1234, s.Minutes);
        Assert.Equal(5242880L * 1024, s.ReportedFlowBytes); // 5 GiB as shown
        Assert.Equal(10L << 30, s.ActualFlowBytes);         // real usage is twice the shown value
        Assert.Equal(12.3456m, s.BalanceYuan);
    }

    [Fact]
    public void ParsesAcrossLines()
    {
        const string html = "time='60';flow='1024';\n<br>\nfee='10000'";
        Assert.Equal(new CampusStatus(60, 1048576, 1m), CampusPortal.Parse(html));
    }

    [Fact]
    public void ParsesGbkBytesWithoutAGbkDecoder()
    {
        // "<title>上网信息</title>" in GBK followed by the ASCII fields; .NET has no GBK decoder by default.
        byte[] gbkTitle = [0x3C, 0x74, 0x69, 0x74, 0x6C, 0x65, 0x3E, 0xC9, 0xCF, 0xCD, 0xF8, 0xD0, 0xC5, 0xCF, 0xA2, 0x3C, 0x2F, 0x74, 0x69, 0x74, 0x6C, 0x65, 0x3E, 0x0A];
        byte[] body = [.. gbkTitle, .. System.Text.Encoding.ASCII.GetBytes("time='90  ';flow='2048  ';fsele=1;fee='5000';")];
        Assert.Equal(new CampusStatus(90, 2048L * 1024, 0.5m), CampusPortal.Parse(body));
    }

    [Fact]
    public void ActualFlowIsTwiceTheReportedFlow()
    {
        // Example from the user: the page shows 285.922 MB while 571.844 MB were used.
        var s = CampusPortal.Parse("time='1';flow='292784';fee='0'")!;
        Assert.Equal(285.922, s.ReportedFlowBytes / 1048576.0, 3);
        Assert.Equal(571.844, s.ActualFlowBytes / 1048576.0, 3);
        // The 200 GB monthly limit is reached when the page shows 100 GB.
        var limit = CampusPortal.Parse($"time='1';flow='{100L * 1024 * 1024}';fee='0'")!;
        Assert.Equal(200L << 30, limit.ActualFlowBytes);
    }

    [Fact]
    public void OverflowingNumbersAreRejectedNotThrown() =>
        Assert.Null(CampusPortal.Parse("time='99999999999999';flow='1';fee='1'"));

    [Fact]
    public void ReturnsNullWhenNotLoggedInPage() =>
        Assert.Null(CampusPortal.Parse("<html><title>上网登录页</title></html>"));
}

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(512, "512B")]
    [InlineData(1258291, "1.2M")]
    [InlineData(26214400, "25M")]
    [InlineData(1395864371, "1.3G")]
    public void Compact(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Compact(bytes));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(12595, "12.3 KB")]
    [InlineData(1310720, "1.25 MB")]
    [InlineData(3650722201, "3.40 GB")]
    public void Size(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Size(bytes));
}
