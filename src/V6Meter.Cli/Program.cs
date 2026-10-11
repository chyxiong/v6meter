using System.Text;
using V6Meter.Cli;
using V6Meter.Core;
using V6Meter.Core.Aggregation;
using V6Meter.Core.Config;
using V6Meter.Core.Etw;
using V6Meter.Core.Interfaces;
using V6Meter.Core.Model;

// Debug console.
//   (no args)            print v4/v6 rates, categories, process ranking and unattributed traffic every second
//   --seconds N          exit after N seconds and print a cumulative per-process summary
//   --top N              rows in the per-second ranking (default 8)
//   --quiet              no per-second output (summary only, on exit / Ctrl+C)
//   --summary-every M    also print the cumulative summary every M minutes (long runs, acceptance test 7)
//   --config path        config.json to use (default: next to the exe; created if missing)
//   --interfaces         print how each network adapter is classified, then exit (no admin needed)
//   --campus-check       IPv4 connectivity probe, reachable campus gateways (and whether logged in on each) and usage, without logging in
//   --campus-fake-login [gateway]  login with a made-up account to see the failure reply (default 172.18.2.2;
//                        never use the gateway you are logged in on)
//   --dump / --dualstack / --procstart   M0 experiments, see Experiments.cs
//   --clock [seconds]    ETW timestamp vs Stopwatch clock, delivery lag, per-second bytes by arrival vs by timestamp
//   --blast <seconds> [chunk]            loopback TCP load generator for the M2 CPU test (no ETW session)

Console.OutputEncoding = Encoding.UTF8;
var opts = ParseArgs(args);

if (opts.ContainsKey("dump") || opts.ContainsKey("dualstack") || opts.ContainsKey("procstart"))
    return await Experiments.RunAsync(opts);

if (opts.TryGetValue("clock", out var clock))
    return await Experiments.ClockAsync(clock.Length > 0 ? int.Parse(clock[0]) : 15);

if (opts.TryGetValue("blast", out var blast))
    return await Experiments.LoopbackBlastAsync(int.Parse(blast[0]), blast.Length > 1 ? int.Parse(blast[1]) : 65536);

if (opts.TryGetValue("campus-fake-login", out var fakeLogin))
{
    // Sends a login with a made-up account (never a real one) to see how a gateway answers a failure.
    // Use a gateway this computer is NOT logged in on: a failed attempt may disturb the current session.
    string gw = fakeLogin.Length > 0 ? fakeLogin[0] : "172.18.2.2";
    if (!V6Meter.Core.Campus.CampusPortal.Gateways.Contains(gw))
    {
        Console.Error.WriteLine($"只能是这几个网关之一：{string.Join(", ", V6Meter.Core.Campus.CampusPortal.Gateways)}");
        return 1;
    }
    using var net = new V6Meter.Core.Campus.CampusNetwork();
    var reply = await net.LoginAsync(gw, new V6Meter.Core.Campus.CampusAccount("v6meter-test", "wrong-password"), default);
    Console.WriteLine($"{gw}：{reply.Outcome}（{reply.Message}）");
    return 0;
}

if (opts.ContainsKey("campus-check"))
{
    // What auto-login would see right now, without logging in.
    using var net = new V6Meter.Core.Campus.CampusNetwork();
    var probe = await net.ProbeAsync(default);
    Console.WriteLine($"IPv4 连通性：{(probe.Online ? "在线" : "不在线")}{(probe.RedirectGateway is { } rg ? $"，被重定向到 {rg}" : "")}");
    var gateways = await net.CheckGatewaysAsync(default);
    Console.WriteLine($"能访问的校园网网关：{(gateways.Count == 0 ? "无" : string.Join(", ", gateways.Select(s => $"{s.Gateway}（{(s.LoggedIn ? "本机已登录" : "未登录")}）")))}");
    try
    {
        var usage = await V6Meter.Core.Campus.CampusPortal.QueryAsync();
        Console.WriteLine($"学校记录：已用 {usage.ActualFlowBytes / 1073741824.0:F3} GB（显示值 ×2）");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
    {
        Console.WriteLine($"学校记录：查不到（{ex.Message}）");
    }
    return 0;
}

if (opts.ContainsKey("interfaces"))
{
    PrintInterfaces(InterfaceMapBuilder.Build());
    return 0;
}

int seconds = opts.TryGetValue("seconds", out var s) ? int.Parse(s[0]) : int.MaxValue;
int top = opts.TryGetValue("top", out var t) ? int.Parse(t[0]) : 8;
string configPath = opts.TryGetValue("config", out var c) ? c[0] : AppConfig.DefaultPath;

if (!AppConfig.TryLoad(configPath, out var config, out string? configLoadError))
    Console.Error.WriteLine($"配置文件有误，已使用默认设置：{configLoadError}");
var classifierOptions = config.ToClassifierOptions(out var configErrors);
foreach (var err in configErrors)
    Console.Error.WriteLine($"配置错误：{err}");

bool quiet = opts.ContainsKey("quiet");
int summaryEvery = opts.TryGetValue("summary-every", out var se) ? int.Parse(se[0]) * 60 : 0;

using var engine = new MeterEngine(classifierOptions, KernelNetworkSession.CliSessionName);
MeterSnapshot? last = null;
int ticks = 0;
engine.Tick += snap =>
{
    last = snap;
    ticks++;
    if (!quiet)
        PrintSecond(snap, top);
    if (summaryEvery > 0 && ticks % summaryEvery == 0)
        PrintSummary(snap);
};

try
{
    engine.Start();
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("创建 ETW 会话被拒绝：请在“以管理员身份运行”的终端中启动。");
    return 1;
}

Console.WriteLine($"v6meter 已启动，配置：{configPath}");
if (engine.Interfaces.HasHyperVExternal)
    Console.WriteLine("提示：检测到物理网卡绑定在 Hyper-V 外部虚拟交换机上，本机地址位于 vEthernet，已按物理网卡统计。");

if (seconds == int.MaxValue)
{
    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    await done.Task;
}
else
{
    await Task.Delay(TimeSpan.FromSeconds(seconds) + TimeSpan.FromSeconds(3.5)); // seconds are closed ~3 s late
}

if (last != null)
    PrintSummary(last);
return 0;

static void PrintSecond(MeterSnapshot snap, int top)
{
    var sec = snap.LastSecond;
    var sb = new StringBuilder();
    sb.AppendLine($"── {snap.Time:HH:mm:ss} ─────────────────────────────────────────────");
    sb.AppendLine($"  v4 ↓{Rate(sec.V4(NetDirection.Recv))} ↑{Rate(sec.V4(NetDirection.Send))}" +
                  $"   (计费 ↓{Rate(sec[TrafficCategory.V4Billed, NetDirection.Recv])}  免费 ↓{Rate(sec[TrafficCategory.V4Free, NetDirection.Recv])})" +
                  $"   v6 ↓{Rate(sec.V6(NetDirection.Recv))} ↑{Rate(sec.V6(NetDirection.Send))}");
    sb.AppendLine($"  NAT64 ↓{Rate(sec[TrafficCategory.V6Nat64, NetDirection.Recv])}  虚拟/代理 ↓{Rate(sec[TrafficCategory.Virtual, NetDirection.Recv])}" +
                  $"  本地 ↓{Rate(sec[TrafficCategory.Local, NetDirection.Recv])}" +
                  $"  未归因 ↓{Rate(snap.Reconcile.UnattributedIn)} ↑{Rate(snap.Reconcile.UnattributedOut)}" +
                  $"  丢事件 {snap.EventsLost}{(snap.EventsLost > 0 ? " ⚠" : "")}");

    var rows = snap.Processes
        .Where(p => p.LastSecond.V4(NetDirection.Recv) + p.LastSecond.V6(NetDirection.Recv) > 0)
        .OrderByDescending(p => p.LastSecond.V4(NetDirection.Recv))
        .ThenByDescending(p => p.LastSecond.V6(NetDirection.Recv))
        .Take(top)
        .ToList();
    if (rows.Count > 0)
    {
        sb.AppendLine($"  {Pad("进程", 34)}{"v4↓/s",11}{"v6↓/s",11}{"v6占比",8}{"累计v4↓",11}{"累计v6↓",11}{"计费v4↓",11}");
        foreach (var p in rows)
            sb.AppendLine($"  {Pad(p.Identity.DisplayName, 34)}{Rate(p.LastSecond.V4(NetDirection.Recv)),11}{Rate(p.LastSecond.V6(NetDirection.Recv)),11}" +
                          $"{V6Share(p.Total),8}{Size(p.Total.V4(NetDirection.Recv)),11}{Size(p.Total.V6(NetDirection.Recv)),11}{Size(p.Total[TrafficCategory.V4Billed, NetDirection.Recv]),11}");
    }
    if (sec[TrafficCategory.Virtual, NetDirection.Recv] + sec[TrafficCategory.Virtual, NetDirection.Send] > 0)
        sb.AppendLine("  提示：开了代理或加速器时，实际走哪条线由代理决定，请看代理进程那一行。");
    Console.Write(sb.ToString());
}

static void PrintSummary(MeterSnapshot snap)
{
    var tot = snap.Total;
    Console.WriteLine();
    Console.WriteLine($"════ 本次累计（截至 {snap.Time:yyyy-MM-dd HH:mm:ss}）════");
    foreach (var cat in Enum.GetValues<TrafficCategory>().Where(x => x != TrafficCategory.Loopback))
        Console.WriteLine($"  {cat,-9} ↓{Size(tot[cat, NetDirection.Recv]),11} ↑{Size(tot[cat, NetDirection.Send]),11}");
    Console.WriteLine($"  其中隧道 ↓{Size(tot.Tunneled(NetDirection.Recv)),11} ↑{Size(tot.Tunneled(NetDirection.Send)),11}");
    var r = snap.ReconcileTotal;
    Console.WriteLine($"  对账（物理网卡）：网卡 ↓{Size(r.NicIn)} ↑{Size(r.NicOut)}   ETW 归因 ↓{Size(r.EtwIn)} ↑{Size(r.EtwOut)}   未归因 ↓{Size(r.NicIn - r.EtwIn)} ↑{Size(r.NicOut - r.EtwOut)}");
    Console.WriteLine($"  ETW 事件 {snap.EventsTotal}   丢事件 {snap.EventsLost}");
    Console.WriteLine($"  {Pad("进程", 40)}{"V4Billed↓",12}{"V4Free↓",12}{"V6↓",12}{"Virtual↓",12}{"v6占比",8}");
    foreach (var p in snap.Processes.OrderByDescending(p => p.Total.V4(NetDirection.Recv) + p.Total.V6(NetDirection.Recv)).Take(20))
        Console.WriteLine($"  {Pad(p.Identity.DisplayName, 40)}{Size(p.Total[TrafficCategory.V4Billed, NetDirection.Recv]),12}{Size(p.Total[TrafficCategory.V4Free, NetDirection.Recv]),12}" +
                          $"{Size(p.Total[TrafficCategory.V6, NetDirection.Recv]),12}{Size(p.Total[TrafficCategory.Virtual, NetDirection.Recv]),12}{V6Share(p.Total),8}");
}

static void PrintInterfaces(InterfaceMap map)
{
    foreach (var i in map.Interfaces.OrderBy(i => i.Kind).ThenBy(i => i.Index))
        Console.WriteLine($"{i.Index,4}  {i.Kind,-10}{(i.IsHyperVExternal ? " [Hyper-V 外部交换机]" : "")}  {i.Name}  ({i.Description})");
    Console.WriteLine($"参与对账的物理网卡：{string.Join(", ", map.AddressedPhysical.Select(i => i.Name))}");
}

static string V6Share(TrafficCounters t)
{
    long v4 = t.V4(NetDirection.Recv), v6 = t.V6(NetDirection.Recv);
    return v4 + v6 == 0 ? "-" : $"{100.0 * v6 / (v4 + v6):F0}%";
}

static string Rate(long b) => Size(b) + "/s";

static string Size(long b) => b switch
{
    >= 1L << 30 => $"{b / 1073741824.0:F2}G",
    >= 1 << 20 => $"{b / 1048576.0:F2}M",
    >= 1 << 10 => $"{b / 1024.0:F1}K",
    _ => $"{b}B",
};

// Pads by display width (CJK characters take two columns).
static string Pad(string text, int width)
{
    var sb = new StringBuilder();
    int w = 0;
    foreach (char ch in text)
    {
        int cw = ch >= 0x1100 ? 2 : 1;
        if (w + cw > width - 1) break;
        sb.Append(ch);
        w += cw;
    }
    return sb.Append(' ', width - w).ToString();
}

static Dictionary<string, string[]> ParseArgs(string[] a)
{
    var r = new Dictionary<string, string[]>();
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        r[a[i][2..]] = a.Skip(i + 1).TakeWhile(x => !x.StartsWith("--")).ToArray();
    }
    return r;
}
