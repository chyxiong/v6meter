namespace V6Meter.App.Ui;

/// <summary>
/// Text colors for the label-less taskbar readout. The line color is the only cue besides position, so:
/// - each color keeps at least 4.5:1 contrast (WCAG AA for small text) against the taskbar. The Win11
///   taskbar is acrylic and picks up the wallpaper: a light one was measured at #EED8D9, so light colors
///   are checked down to #D6D3D3 (v4 4.7, v6 5.0, hot 5.2); the main-window accents only reach ~3:1;
/// - v6 is teal rather than pure green, so orange vs teal stays distinct for red-green color vision
///   deficiencies (simulated CIELAB ΔE ≥ 46 for deuteranopia/protanopia; a pure green pair drops to 18);
/// - the over-threshold v4 line is red *and* bold, so it differs in shape as well as hue.
/// </summary>
internal sealed record TaskbarColors(Color V4, Color V6, Color Hot)
{
    public static readonly TaskbarColors Light = new(
        V4: Color.FromArgb(0x9A, 0x3C, 0x06),
        V6: Color.FromArgb(0x09, 0x5E, 0x68),
        Hot: Color.FromArgb(0xA0, 0x1E, 0x14));

    public static readonly TaskbarColors Dark = new(
        V4: Color.FromArgb(0xFF, 0x9F, 0x5A),
        V6: Color.FromArgb(0x4F, 0xD6, 0xC8),
        Hot: Color.FromArgb(0xFF, 0x6B, 0x61));
}
