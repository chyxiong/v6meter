namespace V6Meter.Core.Alerts;

public sealed record RateAlert(double MiBPerSec, string ProcessKey, string ProcessName);

/// <summary>
/// Raises an alert when the billed download rate stays above the threshold for N consecutive seconds,
/// naming the top process. The same process is not alerted again within the cooldown.
/// </summary>
public sealed class RateAlerter
{
    private readonly Dictionary<string, DateTime> _lastAlert = new();
    private int _secondsAbove;

    public double ThresholdMiBPerSec { get; set; }
    public int SustainSeconds { get; set; }
    public TimeSpan Cooldown { get; set; }

    public RateAlerter(double thresholdMiBPerSec, int sustainSeconds, TimeSpan cooldown)
    {
        ThresholdMiBPerSec = thresholdMiBPerSec;
        SustainSeconds = sustainSeconds;
        Cooldown = cooldown;
    }

    public long ThresholdBytes => (long)(ThresholdMiBPerSec * 1048576);

    /// <summary>True when the last second was above the threshold (tray icon turns red).</summary>
    public bool IsAbove { get; private set; }

    /// <summary>
    /// No notifications before this time ("don't remind me for an hour / today"). The rate is still tracked
    /// and <see cref="IsAbove"/> still updates, so the red highlight keeps working while snoozed.
    /// </summary>
    public DateTime SnoozedUntil { get; set; }

    public bool IsSnoozed(DateTime now) => now < SnoozedUntil;

    public static DateTime OneHourFrom(DateTime now) => now.AddHours(1);

    /// <summary>Midnight that ends <paramref name="now"/>'s day (local time).</summary>
    public static DateTime EndOfDay(DateTime now) => now.Date.AddDays(1);

    /// <param name="billedDownBytes">Billed download bytes in the last second.</param>
    /// <param name="topProcess">Process with the most billed download in that second, if any.</param>
    public RateAlert? Update(DateTime now, long billedDownBytes, (string Key, string Name)? topProcess)
    {
        IsAbove = ThresholdMiBPerSec > 0 && billedDownBytes > ThresholdBytes;
        _secondsAbove = IsAbove ? _secondsAbove + 1 : 0;
        if (_secondsAbove < Math.Max(1, SustainSeconds) || topProcess is not { } top || IsSnoozed(now))
            return null;

        if (_lastAlert.TryGetValue(top.Key, out var last) && now - last < Cooldown)
            return null;
        _lastAlert[top.Key] = now;
        return new RateAlert(billedDownBytes / 1048576.0, top.Key, top.Name);
    }
}
