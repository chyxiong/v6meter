using System.Data;
using V6Meter.App.Ui;
using V6Meter.Core;
using V6Meter.Core.Aggregation;
using V6Meter.Core.Campus;
using V6Meter.Core.Etw;
using V6Meter.Core.Formatting;
using V6Meter.Core.Model;

namespace V6Meter.App;

/// <summary>What the usage card shows.</summary>
/// <param name="RunBytes">Admin: billed IPv4 since start. User mode: estimated IPv4 download since start.</param>
/// <param name="Campus">The last campus query this run, if any (its flow is already the actual, ×2, figure).</param>
internal sealed record UsageView(long RunBytes, DateTime Since, bool UserMode, CampusStatus? Campus, DateTime CampusAt, long QuotaBytes);

/// <summary>
/// Main window. Everything except the process table is custom-painted (no per-value child controls),
/// and nothing is updated while the window is hidden, so a visible window costs one repaint per second.
/// </summary>
internal sealed class MainForm : Form
{
    private const string ProxyHint = "开了代理或加速器时，实际走哪条线由代理决定，请看代理进程那一行。";
    private const string HyperVHint = "检测到物理网卡绑定在 Hyper-V 外部虚拟交换机上，本机流量按 vEthernet 统计。";

    private readonly MeterMode _mode;
    private readonly RateCard _v4;
    private readonly RateCard _v6;
    private readonly InfoCard _userModeInfo = new();
    private readonly UsageCard _usage = new();
    private readonly Banner _banner = new() { Dock = DockStyle.Fill };
    private readonly Card _tableCard = new();
    private readonly ProcessGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly FooterBar _footer = new() { Dock = DockStyle.Fill };
    private readonly ToggleSwitch _autostart = new() { Text = "开机自启", Anchor = AnchorStyles.Right };
    private readonly TableLayoutPanel _bottom = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
    private readonly TableLayoutPanel _root = new() { Dock = DockStyle.Fill, ColumnCount = 1 };
    private readonly TableLayoutPanel _cards = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
    private readonly DataTable _table = new();
    private readonly BindingSource _binding = new();
    private readonly Dictionary<string, DataRow> _rows = new();
    private bool _pendingFirstPaint = true;
    private bool _proxySeen;

    public event EventHandler? QueryCampusClicked;
    public event EventHandler? OpenConfigClicked;
    public event EventHandler? RunAsAdminClicked;
    public event EventHandler<bool>? AutostartToggled;

    public MainForm(MeterMode mode)
    {
        _mode = mode;
        bool user = mode == MeterMode.User;
        _v4 = new RateCard { Title = "IPv4", Badge = user ? "计费 · 估算" : "计费", Accent = p => p.V4 };
        _v6 = new RateCard { Title = "IPv6", Badge = user ? "免费 · 估算" : "免费", Accent = p => p.V6 };
        Text = "v6meter";
        Font = new Font(Theme.UiFamily, 9f);
        // Code-built forms do not auto-scale; size in logical pixels and convert for the monitor DPI.
        ClientSize = new Size(S(1040), S(660));
        MinimumSize = new Size(S(760), S(480));
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        _usage.QueryButton.Click += (s, e) => QueryCampusClicked?.Invoke(this, e);
        if (user)
        {
            _usage.ConfigButton.Text = "以管理员身份运行";
            _usage.ConfigButton.Click += (s, e) => RunAsAdminClicked?.Invoke(this, e);
        }
        else
            _usage.ConfigButton.Click += (s, e) => OpenConfigClicked?.Invoke(this, e);

        int gap = S(12);
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        foreach (var (card, i) in new Card[] { _v4, _v6, _usage }.Select((c, i) => (c, i)))
        {
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, i < 2 ? gap : 0, 0);
            card.Padding = new Padding(S(16), S(14), S(16), S(12));
            _cards.Controls.Add(card, i, 0);
        }

        BuildGrid();
        _tableCard.Dock = DockStyle.Fill;
        _tableCard.Padding = new Padding(S(6), S(6), S(6), S(10));
        _tableCard.Margin = Padding.Empty;
        _tableCard.Controls.Add(_grid);

        _root.Padding = new Padding(S(16), S(14), S(16), S(6));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(168)));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
        _banner.Margin = new Padding(0, gap, 0, 0);
        _cards.Margin = Padding.Empty;
        _tableCard.Margin = new Padding(0, gap, 0, 0);
        _footer.Margin = new Padding(0, S(4), 0, 0);
        _root.Controls.Add(_cards, 0, 0);
        _root.Controls.Add(_banner, 0, 1);
        if (user)
        {
            // No ETW without admin rights: explain instead of showing an empty process table.
            _userModeInfo.Dock = DockStyle.Fill;
            _userModeInfo.Margin = new Padding(0, gap, 0, 0);
            _userModeInfo.Padding = new Padding(S(24));
            _userModeInfo.Button.Click += (s, e) => RunAsAdminClicked?.Invoke(this, e);
            _root.Controls.Add(_userModeInfo, 0, 2);
        }
        else
            _root.Controls.Add(_tableCard, 0, 2);
        _autostart.Toggled += (s, on) => AutostartToggled?.Invoke(this, on);
        _autostart.Size = _autostart.GetPreferredSize(Size.Empty);
        _autostart.Margin = new Padding(0, S(4), 0, 0); // same top offset as the footer text
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bottom.Controls.Add(_footer, 0, 0);
        _bottom.Controls.Add(_autostart, 1, 0);
        _root.Controls.Add(_bottom, 0, 3);
        Controls.Add(_root);

        ApplyTheme();
        Theme.Changed += OnThemeChanged;
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    private void OnThemeChanged()
    {
        if (InvokeRequired)
            BeginInvoke(ApplyTheme);
        else
            ApplyTheme();
    }

    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = _root.BackColor = _cards.BackColor = _bottom.BackColor = p.Background;
        _tableCard.BackColor = _userModeInfo.BackColor = p.Background;
        _usage.BackColor = p.Card; // buttons clear to the card color
        _grid.ApplyTheme();
        Theme.ApplyTitleBar(this);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private void BuildGrid()
    {
        _table.Columns.Add("key", typeof(string));
        _table.Columns.Add("name", typeof(string));
        _table.Columns.Add(ProcessGrid.V4RateColumn, typeof(long));
        _table.Columns.Add(ProcessGrid.V6RateColumn, typeof(long));
        _table.Columns.Add(ProcessGrid.ShareColumn, typeof(double));
        _table.Columns.Add("v4total", typeof(long));
        _table.Columns.Add("v6total", typeof(long));
        _table.Columns.Add("total", typeof(long));
        _table.PrimaryKey = [_table.Columns["key"]!];
        _table.DefaultView.Sort = "v4rate DESC, v6rate DESC, total DESC";

        AddColumn("name", "进程", 26, DataGridViewContentAlignment.MiddleLeft);
        AddColumn(ProcessGrid.V4RateColumn, "v4 ↓/s", 12);
        AddColumn(ProcessGrid.V6RateColumn, "v6 ↓/s", 12);
        AddColumn(ProcessGrid.ShareColumn, "v6 占比", 12);
        AddColumn("v4total", "本次累计 v4", 12);
        AddColumn("v6total", "本次累计 v6", 12);
        AddColumn("total", "总流量", 12);
        _grid.EmptyText = $"还没有软件的下载量超过 {ByteFormat.Size(ProcessRanking.MinTotalBytes)}，超过后会显示在这里";
        _binding.DataSource = _table.DefaultView;
        _grid.DataSource = _binding;
        _grid.DataBindingComplete += (s, e) => { if (!_grid.Focused) _grid.ClearSelection(); };
    }

    private void AddColumn(string property, string header, float weight, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleRight)
    {
        var col = new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            FillWeight = weight,
            SortMode = DataGridViewColumnSortMode.Automatic,
        };
        col.DefaultCellStyle.Alignment = align;
        col.HeaderCell.Style.Alignment = align;
        col.HeaderCell.Style.WrapMode = DataGridViewTriState.False; // a squeezed header clips rather than breaking onto two lines
        _grid.Columns.Add(col);
    }

    public void SetAutostart(bool on) => _autostart.On = on;

    public void UpdateView(MeterSnapshot snap, UsageView usage)
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
            return;


        var sec = snap.LastSecond;
        _v4.Push(sec.V4(NetDirection.Recv), sec.V4(NetDirection.Send));
        _v6.Push(sec.V6(NetDirection.Recv), sec.V6(NetDirection.Send));
        _usage.SetView(usage);

        // Shown for the rest of the run once proxy/VPN traffic adds up to something real, rather than
        // blinking with every second that happens to carry a few packets.
        _proxySeen |= snap.Total[TrafficCategory.Virtual, NetDirection.Recv] + snap.Total[TrafficCategory.Virtual, NetDirection.Send] >= ProcessRanking.MinTotalBytes;
        var hints = new List<string>();
        if (_proxySeen)
            hints.Add(ProxyHint);
        if (snap.Interfaces.HasHyperVExternal)
            hints.Add(HyperVHint);
        _banner.SetLines(hints);

        if (_mode == MeterMode.Admin)
            UpdateTable(snap);

        if (_mode == MeterMode.User)
            _footer.SetSegments([("普通模式：v4/v6 按 IPv4/IPv6 数据包比例估算；同时开着虚拟机或代理加速器时误差会变大", false)]);
        else
            _footer.SetSegments([
                ($"其他（协议头、WSL/虚拟机等）↓ {ByteFormat.Rate(snap.Reconcile.UnattributedIn)}  ↑ {ByteFormat.Rate(snap.Reconcile.UnattributedOut)}", false),
                ($"虚拟网卡/代理 ↓ {ByteFormat.Rate(sec[TrafficCategory.Virtual, NetDirection.Recv])}", false),
                ($"NAT64 ↓ {ByteFormat.Rate(sec[TrafficCategory.V6Nat64, NetDirection.Recv])}", false),
                snap.CollectorDown
                    ? ("ETW 监控会话已停止，正在重新建立", true)
                    : ($"ETW 丢事件 {snap.EventsLost}", snap.EventsLost > 0),
            ]);


        if (_pendingFirstPaint)
        {
            _pendingFirstPaint = false;
            _grid.ClearSelection();
        }
    }

    private void UpdateTable(MeterSnapshot snap)
    {
        // Batch all cell changes into one grid refresh instead of one per changed cell.
        _binding.RaiseListChangedEvents = false;
        _table.BeginLoadData();
        bool changed = false;

        // Only rows over the size floor, capped in number; the smallest totals drop out first.
        var visible = ProcessRanking.Visible(snap.Processes);
        var keep = visible.Select(p => p.Identity.Key).ToHashSet();
        foreach (var key in _rows.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _table.Rows.Remove(_rows[key]);
            _rows.Remove(key);
            changed = true;
        }

        foreach (var p in visible)
        {
            long v4Total = p.Total.V4(NetDirection.Recv), v6Total = p.Total.V6(NetDirection.Recv);
            if (!_rows.TryGetValue(p.Identity.Key, out var row))
            {
                row = _table.NewRow();
                row["key"] = p.Identity.Key;
                _table.Rows.Add(row);
                _rows[p.Identity.Key] = row;
                changed = true;
            }
            changed |= SetIfChanged(row, "name", p.Identity.DisplayName);
            changed |= SetIfChanged(row, ProcessGrid.V4RateColumn, p.LastSecond.V4(NetDirection.Recv));
            changed |= SetIfChanged(row, ProcessGrid.V6RateColumn, p.LastSecond.V6(NetDirection.Recv));
            changed |= SetIfChanged(row, ProcessGrid.ShareColumn, v4Total + v6Total == 0 ? -1.0 : 100.0 * v6Total / (v4Total + v6Total));
            changed |= SetIfChanged(row, "v4total", v4Total);
            changed |= SetIfChanged(row, "v6total", v6Total);
            changed |= SetIfChanged(row, "total", v4Total + v6Total);
        }
        _table.EndLoadData();
        _binding.RaiseListChangedEvents = true;
        if (changed)
            _binding.ResetBindings(metadataChanged: false);
    }

    private static bool SetIfChanged(DataRow row, string column, object value)
    {
        if (row[column].Equals(value))
            return false;
        row[column] = value;
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window only hides it; the tray icon keeps running. Exit from the tray menu.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
