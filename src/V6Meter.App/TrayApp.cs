using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using V6Meter.App.Ui;
using V6Meter.Core;
using V6Meter.Core.Alerts;
using V6Meter.Core.Billing;
using V6Meter.Core.Campus;
using V6Meter.Core.Config;
using V6Meter.Core.Etw;
using V6Meter.Core.Formatting;
using V6Meter.Core.Startup;

namespace V6Meter.App;

/// <summary>
/// Owns everything: the engine, alerts, tray icon, taskbar widget and the main window. Engine ticks arrive
/// on a worker thread and are posted to the UI thread, where all state lives.
/// Admin mode (elevated): ETW, per-process, classified. User mode (not elevated, or ETW failed): NIC totals
/// split into v4/v6 by an estimate; no processes.
/// Usage is only counted for the current run and never stored: the campus portal is the monthly record.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly string _configPath = AppConfig.DefaultPath;
    private readonly SynchronizationContext _ui;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly IMeterEngine _engine;
    private readonly RateAlerter _rateAlerter;
    private readonly DateTime _since = DateTime.Now;
    private long _runBytes;
    private readonly FileSystemWatcher? _configWatcher;
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _campusRefresh = new() { Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds };
    // Addresses change in bursts while a link comes up; query once it has settled.
    private readonly System.Windows.Forms.Timer _networkDebounce = new() { Interval = (int)TimeSpan.FromSeconds(10).TotalMilliseconds };
    private TaskbarWidget? _widget;
    private readonly ToolStripMenuItem _autostartItem = new("开机自启");
    private bool _settingAutostart;
    private AppConfig _config;
    private MainForm? _form;
    private bool _hot;
    private CampusStatus? _campus;
    private DateTime _campusAt;
    private bool _exiting;
    private readonly string? _renderPath;
    private int _ticks;

    public MeterMode Mode => _engine.Mode;

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public TrayApp(bool showWindow = false, string? renderPath = null)
    {
        // Ensure a sync context exists before the message loop starts so ticks can be posted to this thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        // Never fails: built-in defaults are used when the file is missing, unreadable or invalid.
        bool configOk = AppConfig.TryLoad(_configPath, out _config, out string? configLoadError);
        var options = _config.ToClassifierOptions(out var configErrors);
        _rateAlerter = new RateAlerter(_config.AlertThresholdMbPerSec, _config.AlertSustainSeconds, TimeSpan.FromMinutes(_config.AlertCooldownMinutes))
        {
            SnoozedUntil = AppSettings.AlertsSnoozedUntil,
        };

        string? fallbackReason = null;
        if (IsElevated)
        {
            var admin = new MeterEngine(options);
            try
            {
                admin.Start();
                _engine = admin;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                admin.Dispose();
                fallbackReason = ex.Message;
                _engine = new UserModeEngine();
                _engine.Start();
            }
        }
        else
        {
            _engine = new UserModeEngine();
            _engine.Start();
        }
        _engine.Tick += snap => _ui.Post(_ => OnSnapshot(snap), null);

        _menu = BuildMenu();
        _tray = new NotifyIcon
        {
            Icon = _icons.Normal,
            Text = "v6meter",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
        _tray.BalloonTipClicked += (s, e) => { if (_lastNotifyWasRateAlert) AskSnooze(); };

        if (AppSettings.ShowTaskbarWidget)
            CreateWidget();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath)!, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (s, e) => _ui.Post(_ => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }, null);
            _configWatcher.Changed += changed;
            _configWatcher.Created += changed;
            _configWatcher.Deleted += changed; // reload writes and applies the defaults
            _configWatcher.Renamed += (s, e) => changed(s, e);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { }
        _reloadDebounce.Tick += (s, e) => { _reloadDebounce.Stop(); ReloadConfig(); };

        if (!configOk)
            Notify("配置文件有误，已使用默认设置", configLoadError ?? "", ToolTipIcon.Warning);
        else if (configErrors.Count > 0)
            Notify("配置文件有误", string.Join("\n", configErrors), ToolTipIcon.Warning);
        if (fallbackReason != null)
            Notify("已切换到普通模式", $"无法创建 ETW 监控会话（{fallbackReason}），v4/v6 改为估算。", ToolTipIcon.Warning);

        _renderPath = renderPath;
        CheckAutostartLocation();
        _ = AutoQueryCampusAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // never surfaces errors
        _campusRefresh.Tick += async (s, e) => await RefreshCampusSilentlyAsync();
        _networkDebounce.Tick += async (s, e) => { _networkDebounce.Stop(); if (_campus == null) await RefreshCampusSilentlyAsync(); };
        if (renderPath == null)
            _campusRefresh.Start();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        if (showWindow || renderPath != null)
            ShowMainWindow();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (s, e) => ShowMainWindow());
        var widgetItem = new ToolStripMenuItem("在任务栏显示速率") { Checked = AppSettings.ShowTaskbarWidget, CheckOnClick = true };
        widgetItem.CheckedChanged += (s, e) => SetWidgetEnabled(widgetItem.Checked);
        menu.Items.Add(widgetItem);
        _autostartItem.Click += async (s, e) => await SetAutostartAsync(!_autostartItem.Checked);
        menu.Items.Add(_autostartItem);
        menu.Opening += (s, e) => RefreshAutostart();
        menu.Items.Add(BuildAlertMenu(menu));
        menu.Items.Add("打开配置文件", null, (s, e) => OpenConfig());
        if (Mode == MeterMode.User)
            menu.Items.Add("以管理员身份运行（显示进程、区分收费流量）", null, (s, e) => RestartElevated());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (s, e) => Exit());
        return menu;
    }

    /// <summary>"超速提醒" submenu: pause for an hour / for today, or resume. Its labels refresh on open.</summary>
    private ToolStripMenuItem BuildAlertMenu(ContextMenuStrip owner)
    {
        var root = new ToolStripMenuItem("超速提醒");
        var status = new ToolStripMenuItem { Enabled = false };
        var resume = new ToolStripMenuItem("恢复提醒", null, (s, e) => SnoozeAlerts(default));
        root.DropDownItems.Add(status);
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add("1 小时内不再提醒", null, (s, e) => SnoozeAlerts(RateAlerter.OneHourFrom(DateTime.Now)));
        root.DropDownItems.Add("今天不再提醒", null, (s, e) => SnoozeAlerts(RateAlerter.EndOfDay(DateTime.Now)));
        root.DropDownItems.Add(resume);
        owner.Opening += (s, e) =>
        {
            bool snoozed = _rateAlerter.IsSnoozed(DateTime.Now);
            root.Text = snoozed ? "超速提醒（已暂停）" : "超速提醒";
            status.Text = snoozed ? $"已暂停，{SnoozeEndText(_rateAlerter.SnoozedUntil)}恢复" : "提醒已开启";
            resume.Enabled = snoozed;
        };
        return root;
    }

    private static string SnoozeEndText(DateTime until) =>
        until == RateAlerter.EndOfDay(DateTime.Now) ? "明天" : until.Date == DateTime.Today ? $"{until:HH:mm} " : $"{until:M月d日 HH:mm} ";

    /// <summary>Sets (or with <c>default</c> clears) the snooze and remembers it across restarts.</summary>
    private void SnoozeAlerts(DateTime until)
    {
        _rateAlerter.SnoozedUntil = until;
        AppSettings.AlertsSnoozedUntil = until;
    }

    /// <summary>Clicked a rate-alert notification: offer to pause further ones.</summary>
    private void AskSnooze()
    {
        var hour = new TaskDialogButton("1 小时内不再提醒");
        var today = new TaskDialogButton("今天不再提醒");
        var keep = new TaskDialogButton("继续提醒");
        var page = new TaskDialogPage
        {
            Caption = "v6meter",
            Heading = "暂停超速提醒？",
            Text = "暂停期间不再弹出超速通知；收费 IPv4 速度过高时，任务栏上那一行照样会变红。\n之后也可以在托盘右键菜单的“超速提醒”里恢复。",
            Icon = TaskDialogIcon.Information,
            Buttons = { hour, today, keep },
            DefaultButton = keep,
        };
        var choice = TaskDialog.ShowDialog(page);
        if (choice == hour)
            SnoozeAlerts(RateAlerter.OneHourFrom(DateTime.Now));
        else if (choice == today)
            SnoozeAlerts(RateAlerter.EndOfDay(DateTime.Now));
    }

    private void SetWidgetEnabled(bool enabled)
    {
        AppSettings.ShowTaskbarWidget = enabled;
        if (enabled)
            CreateWidget();
        else
        {
            _widget?.Dispose();
            _widget = null;
        }
    }

    private void CreateWidget()
    {
        if (_widget != null)
            return;
        _widget = new TaskbarWidget { RightClickMenu = _menu };
        _widget.LeftClicked += (s, e) => ShowMainWindow();
        _widget.Show();
    }

    private void OnSnapshot(MeterSnapshot snap)
    {
        if (_exiting)
            return;

        // Since this run started. Admin: billed IPv4 by the configured rules. User mode: estimated IPv4 download.
        _runBytes = Mode == MeterMode.Admin ? BillingRules.BilledBytes(snap.Total, _config) : snap.Total.V4(NetDirection.Recv);

        // Rate alert on billed download. Admin mode names the top process; user mode only has the estimated total.
        long billedDown = BillingRules.BilledDown(snap.LastSecond, _config);
        (string Key, string Name)? top = Mode == MeterMode.User
            ? ("*", "")
            : snap.Processes
                .Select(p => (p, b: BillingRules.BilledDown(p.LastSecond, _config)))
                .Where(x => x.b > 0)
                .OrderByDescending(x => x.b)
                .Select(x => ((string Key, string Name)?)(x.p.Identity.Key, x.p.Identity.DisplayName))
                .FirstOrDefault();
        var alert = _rateAlerter.Update(snap.Time, billedDown, top);
        if (alert != null)
            Notify("IPv4 计费流量偏高", (Mode == MeterMode.User
                ? $"IPv4 流量约 {alert.MiBPerSec:F1} MB/s（估算）。以管理员身份运行可以看到是哪个进程。"
                : $"IPv4 计费流量 {alert.MiBPerSec:F1} MB/s，主要来自 {alert.ProcessName}") + "\n点击此通知可暂停提醒", ToolTipIcon.Warning, rateAlert: true);

        if (_rateAlerter.IsAbove != _hot)
        {
            _hot = _rateAlerter.IsAbove;
            _tray.Icon = _hot ? _icons.Hot : _icons.Normal;
        }
        _tray.Text = Tooltip(snap);
        _widget?.SetRates(snap.LastSecond.V4(NetDirection.Recv), snap.LastSecond.V6(NetDirection.Recv), _hot);

        _form?.UpdateView(snap, new UsageView(_runBytes, _since, Mode == MeterMode.User, _campus, _campusAt, QuotaBytes));

        if (_renderPath != null && ++_ticks == 12 && _form != null)
        {
            using var bmp = new Bitmap(_form.Width, _form.Height);
            _form.DrawToBitmap(bmp, new Rectangle(Point.Empty, _form.Size));
            bmp.Save(_renderPath, System.Drawing.Imaging.ImageFormat.Png);
            _widget?.SaveScreenCapture(Path.ChangeExtension(_renderPath, null) + "-taskbar.png");
            Exit();
        }
    }

    private string Tooltip(MeterSnapshot snap)
    {
        string s = $"v4 ↓{ByteFormat.Compact(snap.LastSecond.V4(NetDirection.Recv))} | v6 ↓{ByteFormat.Compact(snap.LastSecond.V6(NetDirection.Recv))}";
        if (Mode == MeterMode.User)
            s += "（估算）";
        return s.Length > 127 ? s[..127] : s;
    }

    private void ShowMainWindow()
    {
        if (_form == null)
        {
            _form = new MainForm(Mode) { Icon = _icons.Normal };
            _form.QueryCampusClicked += async (s, e) => await QueryCampusAsync();
            _form.OpenConfigClicked += (s, e) => OpenConfig();
            _form.RunAsAdminClicked += (s, e) => RestartElevated();
            _form.AutostartToggled += async (s, on) => await SetAutostartAsync(on);
            RefreshAutostart();
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    /// <summary>Starts an elevated copy (UAC prompt) that waits for this process to exit, then exits.</summary>
    private void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--show --after {Environment.ProcessId}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // user cancelled the UAC prompt
        {
            return;
        }
        Exit();
    }

    private void RefreshAutostart()
    {
        bool on = Autostart.GetState() == AutostartState.On;
        _autostartItem.Checked = on;
        _form?.SetAutostart(on);
    }

    /// <summary>
    /// The exe was moved since autostart was turned on, so the task points at a missing file. Repoint it when
    /// we can do so silently; otherwise ask once rather than raising a UAC prompt at startup.
    /// </summary>
    private void CheckAutostartLocation()
    {
        if (_renderPath != null || Autostart.GetState() != AutostartState.Moved)
            return;
        if (IsElevated)
            _ = SetAutostartAsync(true);
        else
            Notify("开机自启需要重新开启", "v6meter 换了位置，原来的开机自启已失效。请在托盘菜单中重新勾选“开机自启”。", ToolTipIcon.Warning);
    }

    // Runs from click handlers (async void upstream): failures end in a message box, never an unhandled exception.
    private async Task SetAutostartAsync(bool on)
    {
        if (_settingAutostart)
            return;
        _settingAutostart = true;
        try
        {
            string? error = await Autostart.SetAsync(on);
            if (!string.IsNullOrEmpty(error))
                MessageBox.Show(_form, $"{(on ? "开启" : "关闭")}开机自启失败：\n{error}", "v6meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, $"{(on ? "开启" : "关闭")}开机自启失败：\n{ex.Message}", "v6meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _settingAutostart = false;
            if (!_exiting)
                RefreshAutostart();
        }
    }

    private bool _querying;

    /// <summary>
    /// Queries the campus portal; on success the result is kept for the usage card. A network error and a
    /// page without the usage fields (e.g. the login page when not logged in) both count as "no data".
    /// </summary>
    private async Task<(CampusStatus? Status, string? Error)> TryQueryCampusAsync()
    {
        try
        {
            var status = await CampusPortal.QueryAsync();
            _campus = status;
            _campusAt = DateTime.Now;
            return (status, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Queries once at startup, silently. When started at logon the network may not be up yet, so a failure
    /// is retried a couple of times; if nothing works the card simply shows no campus figure.
    /// </summary>
    private async Task AutoQueryCampusAsync()
    {
        foreach (var delay in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) })
        {
            await Task.Delay(delay);
            if (_exiting || _campus != null)
                return;
            if (_querying)
                continue; // a manual query is running and reports its own result
            if ((await TryQueryCampusAsync()).Status != null)
                return;
        }
    }

    /// <summary>
    /// Silent background refresh (hourly, and after a network change while there is no figure yet). A failure
    /// keeps the last figure, which is shown with its query time.
    /// </summary>
    private async Task RefreshCampusSilentlyAsync()
    {
        // Its own flag, so a click during a background query is never swallowed.
        if (_exiting || _querying || _refreshing)
            return;
        _refreshing = true;
        try { await TryQueryCampusAsync(); }
        finally { _refreshing = false; }
    }

    private bool _refreshing;

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _ui.Post(_ => { if (_campus == null && !_exiting) { _networkDebounce.Stop(); _networkDebounce.Start(); } }, null);

    // Runs from a button click (async void upstream): every failure must end in a message box, never an unhandled exception.
    private async Task QueryCampusAsync()
    {
        if (_querying)
            return;
        _querying = true;
        try
        {
            await QueryCampusCoreAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, $"查询失败：{ex.Message}", "查询校园网用量", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _querying = false;
        }
    }

    private async Task QueryCampusCoreAsync()
    {
        var (status, error) = await TryQueryCampusAsync();
        if (status == null)
        {
            MessageBox.Show(_form, $"没有查到校园网用量。\n\n请检查电脑是否已连接校园网，并且已经成功登录。\n\n（{error}）",
                "查询校园网用量", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        long quota = QuotaBytes;

        var text = new System.Text.StringBuilder();
        text.AppendLine($"使用时长：{status.Minutes / 60} 小时 {status.Minutes % 60} 分钟");
        text.AppendLine($"已用流量：{CampusSize(status.ActualFlowBytes)}");
        text.AppendLine($"　　（学校系统显示 {CampusSize(status.ReportedFlowBytes)}，实际用量是显示值的 {CampusPortal.ReportedFlowFactor} 倍）");
        if (quota > 0)
            text.AppendLine($"月限额：{ByteFormat.Size(quota)}，已用 {100.0 * status.ActualFlowBytes / quota:F1}%");
        text.AppendLine($"账户余额：{status.BalanceYuan:F2} 元");
        text.AppendLine();
        text.AppendLine(Mode == MeterMode.Admin
            ? $"本次运行（{_since:HH:mm} 起）本工具统计的计费 IPv4：{ByteFormat.Size(_runBytes)}"
            : $"本次运行（{_since:HH:mm} 起）的 IPv4 下行（估算）：{ByteFormat.Size(_runBytes)}");
        text.AppendLine();
        text.Append("提示：" + CampusPortal.HolidayNote);
        MessageBox.Show(_form, text.ToString(), "校园网用量", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private long QuotaBytes => (long)(_config.MonthlyV4QuotaGb * 1073741824);

    /// <summary>Same units as the campus page (MB below 1 GB, three decimals).</summary>
    private static string CampusSize(long bytes) =>
        bytes < 1L << 30 ? $"{bytes / 1048576.0:F3} MB" : $"{bytes / 1073741824.0:F3} GB";

    private void OpenConfig()
    {
        if (!File.Exists(_configPath))
            AppConfig.TryLoad(_configPath, out _, out _); // writes the default file if possible
        if (!File.Exists(_configPath))
        {
            MessageBox.Show(_form, $"无法在程序目录创建配置文件：\n{_configPath}\n\n请把 v6meter 放到有写入权限的文件夹，或以管理员身份运行。", "v6meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_configPath}\"") { UseShellExecute = true });
    }

    private void ReloadConfig()
    {
        // A broken edit keeps the last working config instead of falling back to defaults.
        if (!AppConfig.TryLoad(_configPath, out var cfg, out string? error))
        {
            Notify("配置文件有误，未生效", error ?? "", ToolTipIcon.Warning);
            return;
        }
        // Deleting the file triggers two reloads (deleted, then the default file written back); notify once.
        if (JsonSerializer.Serialize(cfg, AppConfig.JsonOptions) == JsonSerializer.Serialize(_config, AppConfig.JsonOptions))
            return;
        var options = cfg.ToClassifierOptions(out var errors);
        _config = cfg;
        _engine.UpdateOptions(options);
        _rateAlerter.ThresholdMiBPerSec = cfg.AlertThresholdMbPerSec;
        _rateAlerter.SustainSeconds = cfg.AlertSustainSeconds;
        _rateAlerter.Cooldown = TimeSpan.FromMinutes(cfg.AlertCooldownMinutes);
        Notify(errors.Count > 0 ? "配置已重新加载（部分条目有误）" : "配置已重新加载", errors.Count > 0 ? string.Join("\n", errors) : "新的设置已生效。",
            errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _engine.NotifyResumed();
    }

    // NotifyIcon has one notification slot, so a click always belongs to the last one shown.
    private bool _lastNotifyWasRateAlert;

    private void Notify(string title, string text, ToolTipIcon icon, bool rateAlert = false)
    {
        _lastNotifyWasRateAlert = rateAlert;
        _tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, icon);
    }

    private void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _campusRefresh.Dispose();
        _networkDebounce.Dispose();
        _configWatcher?.Dispose();
        _engine.Dispose();
        _widget?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icons.Dispose();
        _form?.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
            Exit();
        base.Dispose(disposing);
    }
}
