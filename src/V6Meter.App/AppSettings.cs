using Microsoft.Win32;

namespace V6Meter.App;

/// <summary>Per-user UI preferences in HKCU (writable in both user and admin mode, unlike the exe folder).</summary>
internal static class AppSettings
{
    private const string KeyPath = @"Software\v6meter";

    public static bool ShowTaskbarWidget
    {
        get => Get(nameof(ShowTaskbarWidget), 1) != 0;
        set => Set(nameof(ShowTaskbarWidget), value ? 1 : 0);
    }

    /// <summary>Rate-alert notifications are off until this local time (kept so "today" survives a restart).</summary>
    public static DateTime AlertsSnoozedUntil
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(nameof(AlertsSnoozedUntil)) is string s
                && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : default;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(nameof(AlertsSnoozedUntil), value.ToString("s", System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
        }
    }

    private static int Get(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is int v ? v : fallback;
    }

    private static void Set(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}
