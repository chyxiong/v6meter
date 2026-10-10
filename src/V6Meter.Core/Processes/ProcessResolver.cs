using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using V6Meter.Core.Etw;

namespace V6Meter.Core.Processes;

public interface IProcessResolver
{
    /// <param name="second">QPC second the traffic happened in (<see cref="Aggregation.FlowAccumulator.SecondOf"/>).</param>
    ProcessIdentity Resolve(uint pid, long second = long.MaxValue);
}

/// <summary>
/// PID → <see cref="ProcessIdentity"/>. Primary source is Kernel-Process start events (recorded the
/// moment a process starts, so short-lived processes like curl still resolve after they exit);
/// processes that were already running are looked up live. Entries are keyed by PID + create time
/// so a reused PID never inherits the previous owner's identity.
/// </summary>
public sealed class ProcessResolver : IProcessResolver
{
    private static readonly TimeSpan ServiceRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NegativeRetry = TimeSpan.FromSeconds(10);

    /// <param name="StartSecond">QPC second of the start event; long.MinValue for processes looked up live.</param>
    /// <param name="Previous">The PID's previous owner: traffic is settled ~3 s late, so a PID reused in that
    /// window must still resolve to the process that sent it.</param>
    private sealed record ProcRecord(DateTime CreateTimeUtc, string? Path, DateTime QueriedUtc, long StartSecond = long.MinValue, ProcRecord? Previous = null)
    {
        /// <summary>Last time traffic resolved to this record; set from the aggregation thread only.</summary>
        public DateTime UsedUtc { get; set; } = QueriedUtc;
    }

    /// <summary>
    /// Every process start on the machine adds a record, so records idle this long are dropped (a monitor
    /// started at logon runs for weeks). A process that is still running is simply looked up live again.
    /// </summary>
    internal static readonly TimeSpan RecordIdleLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<uint, ProcRecord> _byPid = new();
    private DateTime _prunedUtc = DateTime.UtcNow;
    private readonly Dictionary<(uint, DateTime), ProcessIdentity> _identities = new();
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly DevicePaths _devicePaths = new();
    private Dictionary<uint, List<ServiceEntry>> _services = new();
    private DateTime _servicesAt = DateTime.MinValue;

    /// <summary>Called on the ETW thread; only stores the record.</summary>
    public void OnProcessStart(ProcessStartEvent e)
    {
        _byPid.TryGetValue(e.Pid, out var previous);
        long start = e.TimestampQpc > 0 ? Aggregation.FlowAccumulator.SecondOf(e.TimestampQpc) : long.MinValue;
        _byPid[e.Pid] = new ProcRecord(e.CreateTimeUtc, e.ImageName, DateTime.UtcNow, start, previous is null ? null : previous with { Previous = null });
    }

    /// <summary>Called once per second from the aggregation thread (not thread-safe with itself).</summary>
    public ProcessIdentity Resolve(uint pid, long second = long.MaxValue)
    {
        if (pid is 0 or 4)
            return ProcessNaming.System;

        var now = DateTime.UtcNow;
        Prune(now);
        _byPid.TryGetValue(pid, out var rec);
        if (rec != null && second < rec.StartSecond)
        {
            // Traffic from before this process started belongs to the PID's previous owner, if we know it.
            if (rec.Previous == null)
                return ProcessNaming.Unknown(pid);
            rec = rec.Previous;
        }
        else if (rec == null || (rec.Path == null && now - rec.QueriedUtc > NegativeRetry))
        {
            _byPid[pid] = rec = QueryLive(pid, now);
        }
        rec.UsedUtc = now;
        if (rec.Path == null)
            return ProcessNaming.Unknown(pid);

        var key = (pid, rec.CreateTimeUtc);
        if (_identities.TryGetValue(key, out var id) && (id.ExePath == null || !ProcessNaming.IsSvchost(id.ExePath) || now - _servicesAt < ServiceRefresh))
            return id;

        string path = _devicePaths.ToDosPath(rec.Path);
        List<ServiceEntry>? services = null;
        if (ProcessNaming.IsSvchost(path))
        {
            if (now - _servicesAt >= ServiceRefresh || !_services.ContainsKey(pid))
            {
                _services = ServiceMap.Query();
                _servicesAt = now;
            }
            _services.TryGetValue(pid, out services);
        }

        id = ProcessNaming.ForExe(path, Description(path), services);
        _identities[key] = id;
        return id;
    }

    /// <summary>Drops records (and their cached identities) that nothing has used for a while; runs every few minutes.</summary>
    internal void Prune(DateTime now)
    {
        if (now - _prunedUtc < RecordIdleLifetime / 6)
            return;
        _prunedUtc = now;
        foreach (var (pid, rec) in _byPid)
            if (now - rec.UsedUtc > RecordIdleLifetime && now - rec.QueriedUtc > RecordIdleLifetime)
                _byPid.TryRemove(KeyValuePair.Create(pid, rec)); // not if a start event replaced it meanwhile

        var kept = new HashSet<(uint, DateTime)>();
        foreach (var (pid, rec) in _byPid)
        {
            kept.Add((pid, rec.CreateTimeUtc));
            if (rec.Previous != null)
                kept.Add((pid, rec.Previous.CreateTimeUtc));
        }
        foreach (var key in _identities.Keys.Where(k => !kept.Contains(k)).ToList())
            _identities.Remove(key);
    }

    internal int RecordCount => _byPid.Count;

    private string? Description(string path)
    {
        if (!_descriptions.TryGetValue(path, out var d))
        {
            try { d = FileVersionInfo.GetVersionInfo(path).FileDescription; }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { d = null; }
            _descriptions[path] = d;
        }
        return d;
    }

    private static ProcRecord QueryLive(uint pid, DateTime now)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return new ProcRecord(DateTime.MinValue, null, now);
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            string? path = QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            DateTime create = GetProcessTimes(h, out long c, out _, out _, out _) ? DateTime.FromFileTimeUtc(c) : DateTime.MinValue;
            return new ProcRecord(create, path, now);
        }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
