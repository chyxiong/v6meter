using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace V6Meter.App;

/// <summary>Draws the tray icons at runtime: a rounded badge with "6", green normally, red while billed IPv4 is over the threshold.</summary>
internal sealed class TrayIcons : IDisposable
{
    public Icon Normal { get; } = Draw(Color.FromArgb(0x2E, 0x7D, 0x32));
    public Icon Hot { get; } = Draw(Color.FromArgb(0xD3, 0x2F, 0x2F));

    private static Icon Draw(Color fill)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            float r = size.Width / 4f;
            using var path = new GraphicsPath();
            var rect = new RectangleF(0, 0, size.Width - 1, size.Height - 1);
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", size.Height * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("6", font, Brushes.White, new RectangleF(0, 0, size.Width, size.Height + 1), sf);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        Normal.Dispose();
        Hot.Dispose();
    }
}
