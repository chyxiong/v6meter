using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;

namespace V6Meter.Cli;

/// <summary>
/// M0 experiments, kept for re-verification (see docs/testing/M0-verification.md).
///   --dump N [--proc name] [--addr ip] [--seconds S]   print the first N raw Kernel-Network events
///   --dualstack &lt;ipv4&gt; &lt;port&gt;                        DualMode IPv6 socket to an IPv4 address; report which events fire
///   --procstart N [--seconds S]                        print the first N Kernel-Process start events
/// </summary>
internal static class Experiments
{
    /// <summary>
    /// --clock S: is the ETW header timestamp (TimeStampQPC) on the same clock as Stopwatch.GetTimestamp(),
    /// and how late do events arrive? Also compares bytes per second bucketed by arrival vs by timestamp.
    /// </summary>
    public static async Task<int> ClockAsync(int seconds)
    {
        var lags = new List<double>(1 << 20);
        var byArrival = new Dictionary<long, long>();
        var byStamp = new Dictionary<long, long>();
        var gate = new object();
        double f = Stopwatch.Frequency;

        void OnNet(KernelNetEvent e)
        {
            long now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                lags.Add((now - e.TimestampQpc) * 1000.0 / f);
                byArrival[now / Stopwatch.Frequency] = byArrival.GetValueOrDefault(now / Stopwatch.Frequency) + e.Size;
                byStamp[e.TimestampQpc / Stopwatch.Frequency] = byStamp.GetValueOrDefault(e.TimestampQpc / Stopwatch.Frequency) + e.Size;
            }
        }

        using var session = new KernelNetworkSession(OnNet, sessionName: KernelNetworkSession.CliSessionName);
        session.Start();
        Console.WriteLine($"Stopwatch.Frequency={Stopwatch.Frequency} IsHighResolution={Stopwatch.IsHighResolution}");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        lock (gate)
        {
            if (lags.Count == 0) { Console.WriteLine("no events"); return 1; }
            lags.Sort();
            double P(double q) => lags[(int)Math.Min(lags.Count - 1, q * lags.Count)];
            Console.WriteLine($"events={lags.Count} lag ms: min={lags[0]:F1} p50={P(0.5):F1} p90={P(0.9):F1} p99={P(0.99):F1} max={lags[^1]:F1} negative={lags.Count(l => l < 0)}");
            long first = byArrival.Keys.Concat(byStamp.Keys).Min(), last = byArrival.Keys.Concat(byStamp.Keys).Max();
            Console.WriteLine("second  by-arrival(KB)  by-timestamp(KB)");
            for (long s = first; s <= last; s++)
                Console.WriteLine($"{s - first,6}  {byArrival.GetValueOrDefault(s) / 1024,14:N0}  {byStamp.GetValueOrDefault(s) / 1024,16:N0}");
        }
        return 0;
    }

    public static async Task<int> RunAsync(Dictionary<string, string[]> opts)
    {
        int seconds = opts.TryGetValue("seconds", out var s) ? int.Parse(s[0]) : 15;
        int dumpLeft = opts.TryGetValue("dump", out var d) ? int.Parse(d[0]) : 0;
        int procLeft = opts.TryGetValue("procstart", out var ps) ? int.Parse(ps[0]) : 0;
        string? procFilter = opts.TryGetValue("proc", out var pf) ? pf[0] : null;
        IPAddress? addrFilter = opts.TryGetValue("addr", out var af) ? IPAddress.Parse(af[0]) : null;
        string[]? dualStack = opts.TryGetValue("dualstack", out var ds) ? ds : null;

        var map = InterfaceMapBuilder.Build();
        var procNames = new ConcurrentDictionary<uint, string>();
        int selfPid = Environment.ProcessId;
        var selfEvents = new ConcurrentDictionary<string, long>();
        var outLock = new object();

        void OnNet(KernelNetEvent e)
        {
            if (dualStack != null && e.Pid == selfPid)
            {
                string key = $"id={e.EventId} {(e.IsV6 ? "v6" : "v4")} {e.Proto} {e.Direction} hdrPid={(e.HeaderPid == selfPid ? "self" : e.HeaderPid.ToString())} {Fmt(e)}";
                selfEvents.AddOrUpdate(key, e.Size, (_, v) => v + e.Size);
            }
            if (Volatile.Read(ref dumpLeft) <= 0)
                return;
            if (addrFilter != null && !addrFilter.Equals(e.Saddr) && !addrFilter.Equals(e.Daddr))
                return;
            string name = procNames.GetOrAdd(e.Pid, ProcName);
            if (procFilter != null && !name.Equals(procFilter, StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.Decrement(ref dumpLeft) < 0)
                return;
            lock (outLock)
                Console.WriteLine($"id={e.EventId,2} {e.Proto} {e.Direction,-4} size={e.Size,6} pid={e.Pid}({name}) hdrPid={e.HeaderPid}  {Fmt(e)}");
        }

        void OnProcessStart(ProcessStartEvent e)
        {
            if (Interlocked.Decrement(ref procLeft) >= 0)
                lock (outLock)
                    Console.WriteLine($"ProcessStart pid={e.Pid} created={e.CreateTimeUtc:O} image={e.ImageName}");
        }

        using var session = new KernelNetworkSession(OnNet, procLeft > 0 ? OnProcessStart : null, KernelNetworkSession.CliSessionName);
        session.Start();
        Console.WriteLine($"Session '{session.SessionName}' started.");

        if (dualStack != null)
        {
            await Task.Delay(1500); // let the session settle
            RunDualStackTest(IPAddress.Parse(dualStack[0]), int.Parse(dualStack[1]));
            await Task.Delay(3000); // ETW real-time delivery has ~1s latency
            Console.WriteLine($"Events with payload PID == self ({selfPid}):");
            foreach (var kv in selfEvents.OrderBy(k => k.Key))
                Console.WriteLine($"  {kv.Key}  bytes={kv.Value}");
            return 0;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"lost={session.EventsLost}");
        return 0;

        string Fmt(KernelNetEvent e)
        {
            string Tag(IPAddress a) => map.IsLocal(a) ? "[local]" : "[remote]";
            var sa = e.Saddr; var da = e.Daddr;
            return $"saddr={sa}{Tag(sa)}:{e.Sport}  daddr={da}{Tag(da)}:{e.Dport}";
        }
    }

    /// <summary>
    /// Pushes TCP data over 127.0.0.1 as fast as possible. Every send/recv raises a Kernel-Network event,
    /// so this drives the monitor's ETW callback far harder than a real download (the traffic itself is
    /// later classified as Loopback and dropped). Does not open an ETW session; run it in a second process.
    /// </summary>
    public static async Task<int> LoopbackBlastAsync(int seconds, int chunk)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long received = 0;
        var reader = Task.Run(() =>
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = server.Receive(buf)) > 0)
                received += n;
        });
        var data = new byte[chunk];
        long sends = 0;
        var sw = Stopwatch.StartNew();
        while (!cts.IsCancellationRequested)
        {
            client.Send(data);
            sends++;
        }
        client.Shutdown(SocketShutdown.Send);
        await reader;
        double s = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"blast: {received / 1048576.0 / s:F0} MB/s over {s:F1}s, {sends / s:F0} sends/s (chunk {chunk} B)");
        return 0;
    }

    private static string ProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "?"; }
    }

    private static void RunDualStackTest(IPAddress v4, int port)
    {
        using var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        sock.Connect(v4.MapToIPv6(), port);
        Console.WriteLine($"DualMode TCP connected: local={sock.LocalEndPoint} remote={sock.RemoteEndPoint}");
        sock.Send(System.Text.Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {v4}\r\nConnection: close\r\n\r\n"));
        var buf = new byte[65536]; long total = 0; int n;
        while ((n = sock.Receive(buf)) > 0) total += n;
        Console.WriteLine($"TCP received {total} bytes");

        using var udp = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        // Minimal DNS query for "example.com" A record, sent to port 53 of the same host.
        byte[] q = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
        udp.SendTo(q, new IPEndPoint(v4.MapToIPv6(), 53));
        udp.ReceiveTimeout = 2000;
        try { Console.WriteLine($"UDP received {udp.Receive(buf)} bytes"); }
        catch (SocketException ex) { Console.WriteLine($"UDP receive: {ex.SocketErrorCode}"); }
    }
}
