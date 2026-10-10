using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace V6Meter.App.Ui;

internal static class Draw
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
    {
        using var path = Rounded(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>Splits "5.62 MB/s" into ("5.62", "MB/s").</summary>
    public static (string Number, string Unit) Split(string formatted)
    {
        int i = formatted.IndexOf(' ');
        return i < 0 ? (formatted, "") : (formatted[..i], formatted[(i + 1)..]);
    }

    public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
    public const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    public const TextFormatFlags TopLeft = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
}

/// <summary>Rounded, bordered surface. Subclasses paint their content in <see cref="PaintContent"/>.</summary>
internal class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(16);
    }

    protected float Radius => LogicalToDeviceUnits(10);
    protected int S(int logical) => LogicalToDeviceUnits(logical);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.Current;
        e.Graphics.Clear(Parent?.BackColor ?? p.Background);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Draw.Rounded(r, Radius))
        {
            using var fill = new SolidBrush(p.Card);
            g.FillPath(fill, path);
            using var pen = new Pen(p.Border);
            g.DrawPath(pen, path);
        }
        PaintContent(g, p, new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical));
    }

    protected virtual void PaintContent(Graphics g, Palette p, Rectangle content) { }
}

/// <summary>IPv4 / IPv6 tile: big download rate, upload rate and a 60-second download sparkline.</summary>
internal sealed class RateCard : Card
{
    private const int HistoryLength = 60;
    private readonly long[] _history = new long[HistoryLength];
    private int _head;
    private long _down, _up, _historyMax;
    private readonly Font _title = new(Theme.UiFamily, 10f, FontStyle.Bold);
    private readonly Font _tag = new(Theme.UiFamily, 8.5f);
    private readonly Font _big = new(Theme.NumberFamily, 26f, FontStyle.Bold);
    private readonly Font _unit = new(Theme.NumberFamily, 12f);
    private readonly Font _small = new(Theme.UiFamily, 9.5f);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public required string Title { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public required string Badge { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public required Func<Palette, Color> Accent { get; init; }

    public void Push(long down, long up)
    {
        // Idle and flat: nothing visible would change, skip the repaint.
        bool unchanged = down == 0 && up == 0 && _down == 0 && _up == 0 && _historyMax == 0;
        _down = down;
        _up = up;
        _history[_head] = down;
        _head = (_head + 1) % HistoryLength;
        _historyMax = _history.Max();
        if (!unchanged)
            Invalidate();
    }

    protected override void PaintContent(Graphics g, Palette p, Rectangle c)
    {
        var accent = Accent(p);

        // Title row: colored dot, name, tag pill.
        int dot = S(8);
        int titleH = _title.Height;
        Draw.FillRounded(g, accent, new RectangleF(c.X, c.Y + (titleH - dot) / 2f, dot, dot), dot / 2f);
        var titleSize = TextRenderer.MeasureText(g, Title, _title, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Title, _title, new Rectangle(c.X + dot + S(8), c.Y, titleSize.Width, titleH), p.Text, Draw.Left);
        var tagSize = TextRenderer.MeasureText(g, Badge, _tag, Size.Empty, TextFormatFlags.NoPadding);
        var tagRect = new Rectangle(c.X + dot + S(8) + titleSize.Width + S(8), c.Y + (titleH - tagSize.Height - S(4)) / 2, tagSize.Width + S(12), tagSize.Height + S(4));
        Draw.FillRounded(g, Color.FromArgb(p.IsDark ? 60 : 30, accent), tagRect, tagRect.Height / 2f);
        TextRenderer.DrawText(g, Badge, _tag, tagRect, accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Big download rate with arrow and unit.
        var (num, unit) = Draw.Split(V6Meter.Core.Formatting.ByteFormat.Rate(_down));
        int y = c.Y + titleH + S(6);
        var arrow = "↓ ";
        var arrowSize = TextRenderer.MeasureText(g, arrow, _unit, Size.Empty, TextFormatFlags.NoPadding);
        var numSize = TextRenderer.MeasureText(g, num, _big, Size.Empty, TextFormatFlags.NoPadding);
        int baseline = y + numSize.Height;
        TextRenderer.DrawText(g, arrow, _unit, new Point(c.X, baseline - arrowSize.Height - S(4)), accent, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, num, _big, new Point(c.X + arrowSize.Width, y), p.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, unit, _unit, new Point(c.X + arrowSize.Width + numSize.Width + S(4), baseline - arrowSize.Height - S(4)), p.TextSecondary, TextFormatFlags.NoPadding);

        // Upload, right-aligned on the title row.
        string upText = "↑ " + V6Meter.Core.Formatting.ByteFormat.Rate(_up);
        TextRenderer.DrawText(g, upText, _small, new Rectangle(c.X, c.Y, c.Width, titleH), p.TextSecondary, Draw.Right);

        // Sparkline across the bottom.
        int sparkTop = baseline + S(8);
        var spark = new RectangleF(c.X, sparkTop, c.Width, c.Bottom - sparkTop);
        if (spark.Height >= S(12))
            PaintSparkline(g, spark, accent);
    }

    private void PaintSparkline(Graphics g, RectangleF r, Color accent)
    {
        long max = Math.Max(64 * 1024, _historyMax);
        var pts = new PointF[HistoryLength];
        for (int i = 0; i < HistoryLength; i++)
        {
            long v = _history[(_head + i) % HistoryLength];
            pts[i] = new PointF(r.X + r.Width * i / (HistoryLength - 1), r.Bottom - (float)(r.Height * v / (double)max));
        }
        using var area = new GraphicsPath();
        area.AddLine(r.X, r.Bottom, pts[0].X, pts[0].Y);
        area.AddLines(pts);
        area.AddLine(pts[^1].X, pts[^1].Y, r.Right, r.Bottom);
        area.CloseFigure();
        using (var fill = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(70, accent), Color.FromArgb(5, accent), LinearGradientMode.Vertical))
            g.FillPath(fill, area);
        using var pen = new Pen(accent, LogicalToDeviceUnits(2)) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose(); _tag.Dispose(); _big.Dispose(); _unit.Dispose(); _small.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>User-mode placeholder for the process table: what is missing and how to get it.</summary>
internal sealed class InfoCard : Card
{
    private readonly Font _title = new(Theme.UiFamily, 12f, FontStyle.Bold);
    private readonly Font _body = new(Theme.UiFamily, 9.5f);

    public FlatButton Button { get; } = new() { Text = "以管理员身份运行" };

    public InfoCard() => Controls.Add(Button);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Button.Size = Button.GetPreferredSize(Size.Empty);
        Button.Location = new Point(Padding.Left, Padding.Top + _title.Height + S(12) + _body.Height * 4 + S(16));
    }

    protected override void PaintContent(Graphics g, Palette p, Rectangle c)
    {
        TextRenderer.DrawText(g, "普通模式", _title, new Point(c.X, c.Y), p.Text, TextFormatFlags.NoPadding);
        const string body =
            "当前没有管理员权限，无法使用 ETW 采集每个连接的流量。\n" +
            "上方的 v4/v6 速率是按 IPv4/IPv6 数据包比例拆分网卡总流量得到的估算值。\n" +
            "以管理员身份运行后，可以准确区分收费和免费流量，并看到每个软件走的是哪条线。";
        TextRenderer.DrawText(g, body, _body, new Rectangle(c.X, c.Y + _title.Height + S(12), c.Width, _body.Height * 4), p.TextSecondary,
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose(); _body.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Flat rounded button that follows the theme.</summary>
internal sealed class FlatButton : Button
{
    private bool _hover, _pressed;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = new Font(Theme.UiFamily, 9f);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + LogicalToDeviceUnits(24), t.Height + LogicalToDeviceUnits(12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var back = _pressed ? Draw.Mix(p.ButtonHover, p.Text, 0.08) : _hover ? p.ButtonHover : p.ButtonBack;
        Draw.FillRounded(g, back, new RectangleF(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(6));
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? p.Text : p.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Rounded hint banner; hides itself when empty.</summary>
internal sealed class Banner : Control
{
    public Banner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font(Theme.UiFamily, 9f);
        Visible = false;
    }

    public void SetLines(IReadOnlyList<string> lines)
    {
        string text = string.Join("\n", lines);
        if (text == Text && Visible == lines.Count > 0)
            return;
        Text = text;
        Visible = lines.Count > 0;
        Height = TextRenderer.MeasureText(text.Length == 0 ? " " : text, Font).Height + LogicalToDeviceUnits(16);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.FillRounded(g, p.WarningBack, new RectangleF(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(8));
        int pad = LogicalToDeviceUnits(12);
        TextRenderer.DrawText(g, "ⓘ", Font, new Rectangle(pad, 0, pad * 2, Height), p.Warning, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(pad * 2 + LogicalToDeviceUnits(4), 0, Width - pad * 3, Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }
}

/// <summary>Single-line status footer made of segments; segments flagged as alerts are drawn as pills.</summary>
internal sealed class FooterBar : Control
{
    private IReadOnlyList<(string Text, bool Alert)> _segments = [];

    public FooterBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font(Theme.UiFamily, 8.75f);
    }

    public void SetSegments(IReadOnlyList<(string Text, bool Alert)> segments)
    {
        if (_segments.SequenceEqual(segments))
            return;
        _segments = segments;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int x = LogicalToDeviceUnits(4), gap = LogicalToDeviceUnits(18);
        foreach (var (text, alert) in _segments)
        {
            var size = TextRenderer.MeasureText(g, text, Font, Size.Empty, TextFormatFlags.NoPadding);
            if (alert)
            {
                var pill = new Rectangle(x - LogicalToDeviceUnits(8), (Height - size.Height) / 2 - LogicalToDeviceUnits(3), size.Width + LogicalToDeviceUnits(16), size.Height + LogicalToDeviceUnits(6));
                Draw.FillRounded(g, p.WarningBack, pill, pill.Height / 2f);
            }
            TextRenderer.DrawText(g, text, Font, new Rectangle(x, 0, size.Width + 1, Height), alert ? p.Warning : p.TextSecondary, Draw.Left);
            x += size.Width + gap;
        }
    }
}

/// <summary>Label plus pill switch. Clicking raises <see cref="Toggled"/> with the requested value; the owner
/// sets <see cref="On"/> once the change has actually happened (it may need a UAC prompt or fail).</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _on, _hover;

    public event EventHandler<bool>? Toggled;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font(Theme.UiFamily, 8.75f);
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool On
    {
        get => _on;
        set { if (_on != value) { _on = value; Invalidate(); } }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(t.Width + LogicalToDeviceUnits(8 + 34 + 4), Math.Max(t.Height, LogicalToDeviceUnits(18)) + LogicalToDeviceUnits(4));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Toggled?.Invoke(this, !_on);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int sw = LogicalToDeviceUnits(34), sh = LogicalToDeviceUnits(18);
        var track = new RectangleF(Width - sw - LogicalToDeviceUnits(2), (Height - sh) / 2f, sw, sh);
        var back = _on ? p.V6 : _hover ? Draw.Mix(p.ButtonHover, p.Text, 0.12) : Draw.Mix(p.ButtonBack, p.Text, 0.12);
        Draw.FillRounded(g, back, track, sh / 2f);
        float knob = sh - LogicalToDeviceUnits(6);
        float kx = _on ? track.Right - knob - LogicalToDeviceUnits(3) : track.X + LogicalToDeviceUnits(3);
        using (var b = new SolidBrush(_on ? Color.White : p.TextSecondary))
            g.FillEllipse(b, kx, track.Y + LogicalToDeviceUnits(3), knob, knob);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, (int)track.X - LogicalToDeviceUnits(8), Height), p.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}