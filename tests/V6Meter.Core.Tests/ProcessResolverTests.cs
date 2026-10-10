using System.Diagnostics;
using V6Meter.Core.Etw;
using V6Meter.Core.Processes;

namespace V6Meter.Core.Tests;

public class ProcessResolverTests
{
    // A PID no live process has, so nothing is looked up from the system.
    private const uint Pid = 0xFFFFFFF0;

    private static ProcessStartEvent Start(string path, long second) =>
        new ProcessStartEvent(Pid, DateTime.UtcNow, path) { TimestampQpc = second * Stopwatch.Frequency };

    [Fact]
    public void ReusedPidResolvesByTheSecondTheTrafficHappened()
    {
        var r = new ProcessResolver();
        r.OnProcessStart(Start(@"C:\old\old.exe", 100));
        r.OnProcessStart(Start(@"C:\new\new.exe", 105)); // old exited, PID reused before its traffic was settled

        Assert.Equal(@"C:\old\old.exe", r.Resolve(Pid, 103).ExePath);
        Assert.Equal(@"C:\new\new.exe", r.Resolve(Pid, 105).ExePath);
        Assert.Equal(@"C:\new\new.exe", r.Resolve(Pid).ExePath);
    }

    [Fact]
    public void IdleRecordsAreDroppedSoTheTableDoesNotGrowForever()
    {
        var r = new ProcessResolver();
        r.OnProcessStart(Start(@"C:\old\old.exe", 100));
        r.Prune(DateTime.UtcNow + ProcessResolver.RecordIdleLifetime / 2);
        Assert.Equal(1, r.RecordCount);
        r.Prune(DateTime.UtcNow + ProcessResolver.RecordIdleLifetime + TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.RecordCount);
    }

    [Fact]
    public void TrafficBeforeTheOnlyKnownStartIsNotGivenToIt()
    {
        var r = new ProcessResolver();
        r.OnProcessStart(Start(@"C:\new\new.exe", 105));
        Assert.Null(r.Resolve(Pid, 103).ExePath);
    }
}
