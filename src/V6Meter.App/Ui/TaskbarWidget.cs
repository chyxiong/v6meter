using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using V6Meter.Core.Formatting;

namespace V6Meter.App.Ui;

/// <summary>
/// Two-line v4/v6 download readout sitting on the taskbar, left of the notification area (and left of
/// TrafficMonitor if it is there).
/// Verified on Win11: a window parented into Shell_TrayWnd is composited underneath the XAML taskbar and
/// is barely visible, so this is a top-level topmost, non-activating, per-pixel-alpha window placed over
/// the taskbar instead. Clicking the taskbar raises it within the topmost band, so topmost is re-asserted
/// on every foreground change and once per second.
/// </summary>
internal sealed class TaskbarWidget : Form
{
    private long _v4, _v6;
    private bool _hot;
    private Rectangle _lastBounds;
    private bool _taskbarLight = ReadTaskbarLight();
    private readonly WinEventDelegate _onForeground;
    private IntPtr _hook;

    public event EventHandler? LeftClicked;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? RightClickMenu { get; set; }

    public TaskbarWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        // Redraw when the bounds change here too: SetRates only redraws on a bounds change it sees itself or
        // on new rates, so an idle 0 B/s readout would otherwise keep a bitmap of the old size.
        _onForeground = (hook, evt, hwnd, obj, child, thread, time) => { if (Reposition()) Render(); };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public void SetRates(long v4Down, long v6Down, bool hot)
    {
        bool changed = v4Down != _v4 || v6Down != _v6 || hot != _hot;
        (_v4, _v6, _hot) = (v4Down, v6Down, hot);
        if (Reposition() || changed)
            Render();
    }

    /// <summary>Places the widget left of the tray area and re-asserts topmost. Returns true if the bounds changed.</summary>
    private bool Reposition()
    {
        if (IsDisposed || !IsHandleCreated)
            return false;
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tr) || !IsWindowVisible(tray) || FullscreenAppActive())
        {
            HideWidget();
            return false;
        }
        var screen = Screen.FromHandle(tray).Bounds;
        bool horizontalBottom = tr.R - tr.L >= screen.Width / 2 && tr.B >= screen.Bottom - 1 && tr.T > screen.Top + screen.Height / 2;
        bool autoHidden = tr.T >= screen.Bottom - LogicalToDeviceUnits(4);
        if (!horizontalBottom || autoHidden)
        {
            HideWidget();
            return false;
        }

        int anchor = tr.R;
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var nr))
            anchor = nr.L;
        // Other taskbar widgets (TrafficMonitor uses a #32770 window) sit left of the tray; go left of them.
        for (IntPtr c = FindWindowEx(tray, IntPtr.Zero, "#32770", null); c != IntPtr.Zero; c = FindWindowEx(tray, c, "#32770", null))
            if (IsWindowVisible(c) && GetWindowRect(c, out var cr) && cr.R <= anchor + 2)
                anchor = Math.Min(anchor, cr.L);

        int h = tr.B - tr.T;
        int w = MeasureWidth();
        var bounds = new Rectangle(anchor - w - LogicalToDeviceUnits(6), tr.T, w, h);
        bool changed = bounds != _lastBounds;
        _lastBounds = bounds;
        SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (!Visible)
            Visible = true;
        return changed;
    }

    private void HideWidget()
    {
        if (Visible)
            Visible = false;
        _lastBounds = Rectangle.Empty;
    }

    // Sized to match TrafficMonitor's taskbar text (measured: 20 px glyph rows, 28 px line pitch at 175%).
    private const float FontPoints = 6.25f;
    private const int LinePitch = 16; // logical px

    private Font NewFont(FontStyle style = FontStyle.Regular) => new(Theme.NumberFamily, FontPoints * DeviceDpi / 96f, style, GraphicsUnit.Point, 1, false);

    private int MeasureWidth()
    {
        // Measure on a 96-dpi bitmap like Render() draws (fonts are pre-scaled to the monitor DPI), and use a
        // fixed width for the widest value so the widget does not jitter as numbers change.
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var bold = NewFont(FontStyle.Bold); // the hot v4 line is bold, so size for that
        float width = g.MeasureString("888.88 MB/s", bold, PointF.Empty, StringFormat.GenericTypographic).Width;
        return (int)Math.Ceiling(width) + LogicalToDeviceUnits(12);
    }

    private void Render()
    {
        if (_lastBounds.IsEmpty)
            return;
        int w = _lastBounds.Width, h = _lastBounds.Height;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // Alpha 1 keeps the whole rectangle clickable while staying invisible.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var colors = _taskbarLight ? TaskbarColors.Light : TaskbarColors.Dark;
            using var regular = NewFont();
            using var bold = NewFont(FontStyle.Bold);
            float lineH = LogicalToDeviceUnits(LinePitch);
            float top = (h - lineH * 2) / 2f;
            // No labels: v4 is always the top line and orange, v6 the bottom line and teal.
            DrawLine(g, _v4, _hot ? colors.Hot : colors.V4, _hot ? bold : regular, top, lineH, w);
            DrawLine(g, _v6, colors.V6, regular, top + lineH, lineH, w);
        }
        Present(bmp);
    }

    private void DrawLine(Graphics g, long rate, Color color, Font font, float y, float lineH, int w)
    {
        using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var brush = new SolidBrush(color);
        g.DrawString(ByteFormat.Rate(rate), font, brush, new RectangleF(0, y, w - LogicalToDeviceUnits(6), lineH), sf);
    }

    private void Present(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = _lastBounds.X, y = _lastBounds.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Debug aid (--render-ui): what is on screen where the widget sits.</summary>
    public void SaveScreenCapture(string path)
    {
        if (_lastBounds.IsEmpty)
            return;
        using var bmp = new Bitmap(_lastBounds.Width, _lastBounds.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr dst = g.GetHdc(), screen = GetDC(IntPtr.Zero);
            BitBlt(dst, 0, 0, bmp.Width, bmp.Height, screen, _lastBounds.X, _lastBounds.Y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            ReleaseDC(IntPtr.Zero, screen);
            g.ReleaseHdc(dst);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            LeftClicked?.Invoke(this, EventArgs.Empty);
        else if (e.Button == MouseButtons.Right)
            RightClickMenu?.Show(Cursor.Position);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
            return;
        bool light = ReadTaskbarLight();
        if (light != _taskbarLight && IsHandleCreated)
        {
            _taskbarLight = light;
            BeginInvoke(Render);
        }
    }

    /// <summary>The taskbar follows the *system* theme, not the app theme.</summary>
    private static bool ReadTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    private static bool FullscreenAppActive() =>
        SHQueryUserNotificationState(out int state) == 0 && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_hook != IntPtr.Zero)
                UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, ULW_ALPHA = 2;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
