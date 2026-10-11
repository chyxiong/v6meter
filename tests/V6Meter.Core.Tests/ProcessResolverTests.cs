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
    public void RevalidationCatchesAPidReusedWhileStartEventsWereMissed()
    {
        // The cache says this PID is old.exe, but a different process (this test host) now holds it: its start
        // event was "missed". Without revalidation the stale name sticks; with it the live process wins.
        uint pid = (uint)Environment.ProcessId;
        var r = new ProcessResolver();
        r.OnProcessStart(new ProcessStartEvent(pid, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), @"C:\old\old.exe"));
        Assert.Equal(@"C:\old\old.exe", r.Resolve(pid).ExePath);

        r.Revalidate();
        Assert.Equal(Environment.ProcessPath, r.Resolve(pid).ExePath, ignoreCase: true);
    }

    [Fact]
    public void RevalidationKeepsTheRecordWhenTheProcessIsGone()
    {
        var r = new ProcessResolver();
        r.OnProcessStart(Start(@"C:\old\old.exe", 100)); // a PID no live process has
        r.Revalidate();
        Assert.Equal(@"C:\old\old.exe", r.Resolve(Pid, 105).ExePath); // exited: its last traffic still has its name
    }

    [Fact]
    public void TrafficBeforeTheOnlyKnownStartIsNotGivenToIt()
    {
        var r = new ProcessResolver();
        r.OnProcessStart(Start(@"C:\new\new.exe", 105));
        Assert.Null(r.Resolve(Pid, 103).ExePath);
    }
}
