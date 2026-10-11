using V6Meter.Core.Formatting;

namespace V6Meter.App.Ui;

/// <summary>
/// Usage tile: IPv4 used since v6meter started (nothing is kept across runs; the school's own record is the
/// monthly figure) and, once queried, the school's monthly usage against the quota. Plus three actions
/// (query usage, log in, config / run as admin) that switch to short captions when the card is narrow.
/// </summary>
internal sealed class UsageCard : Card
{
    private readonly Font _title = new(Theme.UiFamily, 10f, FontStyle.Bold);
    private readonly Font _big = new(Theme.NumberFamily, 22f, FontStyle.Bold);
    private readonly Font _unit = new(Theme.NumberFamily, 11f);
    private readonly Font _small = new(Theme.UiFamily, 8.75f);
    private UsageView? _view;

    public FlatButton QueryButton { get; } = new() { Text = "查询校园网用量" };
    public FlatButton LoginButton { get; } = new() { Text = "登录校园网" };
    public FlatButton ConfigButton { get; } = new() { Text = "打开配置" };

    // Full and short captions; the short ones are used when the full row does not fit the card.
    private readonly Dictionary<FlatButton, (string Full, string Short)> _labels = new();
    private bool _layingOut;

    public UsageCard()
    {
        SetLabels(QueryButton, "查询校园网用量", "查询用量");
        SetLabels(LoginButton, "登录校园网", "登录");
        SetLabels(ConfigButton, "打开配置", "配置");
        Controls.Add(QueryButton);
        Controls.Add(LoginButton);
        Controls.Add(ConfigButton);
    }

    public void SetLabels(FlatButton button, string full, string shortText)
    {
        _labels[button] = (full, shortText);
        PerformLayout();
    }

    /// <summary>Shows a temporary caption (e.g. "登录中…") and disables the button; null restores it.</summary>
    public void SetBusy(FlatButton button, string? busyText)
    {
        button.Enabled = busyText == null;
        button.Tag = busyText;
        PerformLayout();
    }

    public void SetView(UsageView v)
    {
        if (v == _view)
            return;
        _view = v;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_layingOut)
            return; // changing Text below re-enters layout
        _layingOut = true;
        try
        {
            var row = new[] { QueryButton, LoginButton, ConfigButton };
            int gap = S(8), available = Width - Padding.Horizontal;
            foreach (bool compact in new[] { false, true })
            {
                foreach (var b in row)
                {
                    var (full, shortText) = _labels.TryGetValue(b, out var l) ? l : (b.Text, b.Text);
                    b.Text = b.Tag as string ?? (compact ? shortText : full);
                    b.Size = b.GetPreferredSize(Size.Empty);
                }
                if (row.Sum(b => b.Width) + gap * (row.Length - 1) <= available)
                    break;
            }
            int y = Height - Padding.Bottom - QueryButton.Height;
            int x = Width - Padding.Right;
            for (int i = row.Length - 1; i >= 0; i--) // right-aligned
            {
                x -= row[i].Width;
                row[i].Location = new Point(x, y);
                x -= gap;
            }
        }
        finally { _layingOut = false; }
    }

    protected override void PaintContent(Graphics g, Palette p, Rectangle c)
    {
        if (_view is not { } v)
            return;

        int titleH = _title.Height;
        TextRenderer.DrawText(g, v.UserMode ? "本次运行 IPv4 下行" : "本次运行计费 IPv4", _title, new Rectangle(c.X, c.Y, c.Width, titleH), p.Text, Draw.Left);
        string since = v.Since.Date == DateTime.Today ? $"{v.Since:HH:mm} 起" : $"{v.Since:M月d日 HH:mm} 起";
        TextRenderer.DrawText(g, v.UserMode ? $"估算 · {since}" : since, _small, new Rectangle(c.X, c.Y, c.Width, titleH), p.TextSecondary, Draw.Right);

        var (num, unit) = Draw.Split(ByteFormat.Size(v.RunBytes));
        int y = c.Y + titleH + S(6);
        var numSize = TextRenderer.MeasureText(g, num, _big, Size.Empty, TextFormatFlags.NoPadding);
        var unitSize = TextRenderer.MeasureText(g, unit, _unit, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, num, _big, new Point(c.X, y), p.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, unit, _unit, new Point(c.X + numSize.Width + S(4), y + numSize.Height - unitSize.Height - S(3)), p.TextSecondary, TextFormatFlags.NoPadding);
        y += numSize.Height + S(8);

        string note;
        if (v.Campus is { } campus)
        {
            // The school's monthly figure (already ×2) against the quota.
            if (v.QuotaBytes > 0)
            {
                double frac = Math.Clamp(campus.ActualFlowBytes / (double)v.QuotaBytes, 0, 1);
                var color = frac >= 0.95 ? p.Danger : frac >= 0.8 ? p.Warning : p.V4;
                float h = S(6);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Draw.FillRounded(g, p.ButtonBack, new RectangleF(c.X, y, c.Width, h), h / 2);
                if (frac > 0)
                    Draw.FillRounded(g, color, new RectangleF(c.X, y, Math.Max(h, (float)(c.Width * frac)), h), h / 2);
                y += (int)h + S(6);
            }
            // One line when the bar is shown (two lines would run into the buttons).
            note = v.QuotaBytes > 0
                ? $"学校记录 {ByteFormat.Size(campus.ActualFlowBytes)} / {ByteFormat.Size(v.QuotaBytes)}（{v.CampusAt:HH:mm}）"
                : $"学校记录本月已用 {ByteFormat.Size(campus.ActualFlowBytes)}（{v.CampusAt:HH:mm} 查询）";
        }
        else
            note = v.LoginNote ?? "关闭后清零。本月用量以学校记录为准，点“查询校园网用量”查看";

        int noteBottom = QueryButton.Top - S(4);
        TextRenderer.DrawText(g, note, _small, new Rectangle(c.X, y, c.Width, Math.Max(0, noteBottom - y)), p.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose(); _big.Dispose(); _unit.Dispose(); _small.Dispose();
        }
        base.Dispose(disposing);
    }
}
