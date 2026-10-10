using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace V6Meter.App.Ui;

internal sealed record Palette(
    bool IsDark,
    Color Background,
    Color Card,
    Color Border,
    Color Text,
    Color TextSecondary,
    Color V4,          // billed IPv4: warm
    Color V6,          // free IPv6: green
    Color Danger,
    Color Warning,
    Color WarningBack,
    Color Selection,
    Color ButtonBack,
    Color ButtonHover)
{
    public static readonly Palette Light = new(
        IsDark: false,
        Background: Color.FromArgb(0xF3, 0xF4, 0xF6),
        Card: Color.White,
        Border: Color.FromArgb(0xE3, 0xE5, 0xE8),
        Text: Color.FromArgb(0x1A, 0x1C, 0x1F),
        TextSecondary: Color.FromArgb(0x6B, 0x70, 0x78),
        V4: Color.FromArgb(0xE8, 0x6A, 0x10),
        V6: Color.FromArgb(0x1E, 0x9E, 0x5A),
        Danger: Color.FromArgb(0xD9, 0x30, 0x25),
        Warning: Color.FromArgb(0xB2, 0x6A, 0x00),
        WarningBack: Color.FromArgb(0xFF, 0xF4, 0xD6),
        Selection: Color.FromArgb(0xE8, 0xF0, 0xFE),
        ButtonBack: Color.FromArgb(0xF1, 0xF3, 0xF5),
        ButtonHover: Color.FromArgb(0xE4, 0xE7, 0xEB));

    public static readonly Palette Dark = new(
        IsDark: true,
        Background: Color.FromArgb(0x1C, 0x1D, 0x20),
        Card: Color.FromArgb(0x27, 0x29, 0x2D),
        Border: Color.FromArgb(0x36, 0x39, 0x3E),
        Text: Color.FromArgb(0xEC, 0xED, 0xEF),
        TextSecondary: Color.FromArgb(0x9A, 0x9F, 0xA6),
        V4: Color.FromArgb(0xFF, 0x8A, 0x3D),
        V6: Color.FromArgb(0x3D, 0xD6, 0x8C),
        Danger: Color.FromArgb(0xFF, 0x5C, 0x52),
        Warning: Color.FromArgb(0xFF, 0xC1, 0x4D),
        WarningBack: Color.FromArgb(0x3A, 0x31, 0x1C),
        Selection: Color.FromArgb(0x2F, 0x3B, 0x4F),
        ButtonBack: Color.FromArgb(0x33, 0x36, 0x3B),
        ButtonHover: Color.FromArgb(0x3E, 0x42, 0x48));
}

/// <summary>Follows the Windows app light/dark setting.</summary>
internal static class Theme
{
    public static Palette Current { get; private set; } = Detect();

    public static event Action? Changed;

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if (e.Category != UserPreferenceCategory.General)
                return;
            var p = Detect();
            if (p != Current)
            {
                Current = p;
                Changed?.Invoke();
            }
        };
    }

    private static Palette Detect()
    {
        // Debug override for screenshots/tests: V6METER_THEME=dark|light.
        switch (Environment.GetEnvironmentVariable("V6METER_THEME")?.ToLowerInvariant())
        {
            case "dark": return Palette.Dark;
            case "light": return Palette.Light;
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Palette.Dark : Palette.Light;
    }

    public static readonly FontFamily UiFamily = Pick("Microsoft YaHei UI", "Segoe UI");
    public static readonly FontFamily NumberFamily = Pick("Segoe UI Variable Display", "Segoe UI");

    private static FontFamily Pick(params string[] names)
    {
        foreach (var n in names)
            if (FontFamily.Families.Any(f => f.Name == n))
                return new FontFamily(n);
        return FontFamily.GenericSansSerif;
    }

    /// <summary>Win11: title bar in the window's background color; dark title bar on Win10 dark mode.</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!form.IsHandleCreated)
            return;
        var p = Current;
        int dark = p.IsDark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int caption = ColorTranslator.ToWin32(p.Background);
        DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        int text = ColorTranslator.ToWin32(p.Text);
        DwmSetWindowAttribute(form.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    /// <summary>Dark scrollbars etc. for common controls.</summary>
    public static void ApplyControlTheme(Control c)
    {
        if (c.IsHandleCreated)
            SetWindowTheme(c.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
