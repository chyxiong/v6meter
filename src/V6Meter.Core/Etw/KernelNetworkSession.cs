using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace V6Meter.Core.Etw;

/// <summary>
/// Real-time ETW session (custom name, not "NT Kernel Logger") consuming
/// Microsoft-Windows-Kernel-Network send/recv events and, optionally,
/// Microsoft-Windows-Kernel-Process start events. Requires administrator rights.
/// </summary>
public sealed class KernelNetworkSession : IDisposable
{
    public const string DefaultSessionName = "v6meter-session";

    /// <summary>The debug CLI's session, so running it never stops the tray app's (and vice versa).</summary>
    public const string CliSessionName = "v6meter-cli-session";
    public static readonly Guid ProviderGuid = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    private const ulong KeywordIPv4 = 0x10;
    private const ulong KeywordIPv6 = 0x20;

    private readonly TraceEventSession _session;
    private readonly Action<KernelNetEvent> _onNet;
    private readonly Action<ProcessStartEvent>? _onProcessStart;
    private Thread? _thread;

    public KernelNetworkSession(
        Action<KernelNetEvent> onNet,
        Action<ProcessStartEvent>? onProcessStart = null,
        string sessionName = DefaultSessionName,
        int bufferSizeMB = 64)
    {
        _onNet = onNet;
        _onProcessStart = onProcessStart;

        // A crashed previous instance leaves the session alive; stop it before creating a new one.
        if (TraceEventSession.GetActiveSessionNames().Contains(sessionName))
        {
            using var stale = new TraceEventSession(sessionName, TraceEventSessionOptions.Attach);
            stale.Stop(noThrow: true);
        }

        _session = new TraceEventSession(sessionName) { BufferSizeMB = bufferSizeMB, StopOnDispose = true };
        try
        {
            // Throws UnauthorizedAccessException without admin rights; the session itself is already
            // created at that point, so it must be disposed or it would be left running.
            _session.EnableProvider(ProviderGuid, TraceEventLevel.Informational, KeywordIPv4 | KeywordIPv6);
            if (onProcessStart != null)
                _session.EnableProvider(ProcessStartEvent.ProviderGuid, TraceEventLevel.Informational, ProcessStartEvent.KeywordProcess);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
        _session.Source.AllEvents += OnTraceEvent;
    }

    public string SessionName => _session.SessionName;

    private int _lastLost;

    /// <summary>Events dropped by ETW (buffers full). Keeps the last known value once the session is gone.</summary>
    public int EventsLost
    {
        get
        {
            if (HasStopped)
                return _lastLost;
            try { return _lastLost = _session.EventsLost; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return _lastLost; }
        }
    }

    /// <summary>True once event processing has ended (the session was stopped, possibly externally).</summary>
    public bool HasStopped => _thread is { IsAlive: false };

    public void Start()
    {
        _thread = new Thread(() => _session.Source.Process()) { IsBackground = true, Name = "etw-kernel-network" };
        _thread.Start();
    }

    // Runs on the ETW thread for every event: keep it to parsing and a callback, nothing else.
    private unsafe void OnTraceEvent(TraceEvent data)
    {
        var payload = new ReadOnlySpan<byte>((void*)data.DataStart, data.EventDataLength);
        // Raw QPC is "discouraged" because sessions can use other clocks; this one uses QPC (verified with
        // the CLI's --clock: it lines up with Stopwatch.GetTimestamp()), and it is the cheapest timestamp.
#pragma warning disable CS0618
        long qpc = data.TimeStampQPC;
#pragma warning restore CS0618
        if (data.ProviderGuid == ProviderGuid)
        {
            if (KernelNetEvent.TryParse((int)data.ID, data.ProcessID, payload, out var e, qpc))
                _onNet(e);
        }
        else if (_onProcessStart != null && data.ProviderGuid == ProcessStartEvent.ProviderGuid && (int)data.ID == ProcessStartEvent.EventId)
        {
            if (ProcessStartEvent.TryParse(data.Version, payload, out var ps))
                _onProcessStart(ps with { TimestampQpc = qpc });
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(3));
    }
}
