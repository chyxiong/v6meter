namespace V6Meter.Core.Formatting;

/// <summary>Binary units (1 K = 1024), labelled K/M/G like Task Manager.</summary>
public static class ByteFormat
{
    /// <summary>"512 B", "12.3 KB", "1.25 MB", "3.40 GB".</summary>
    public static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / 1073741824.0:F2} GB",
        >= 1 << 20 => $"{b / 1048576.0:F2} MB",
        >= 1 << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };

    public static string Rate(long bytesPerSecond) => Size(bytesPerSecond) + "/s";

    /// <summary>Short form for the tray tooltip: "0", "512B", "12K", "1.2M", "25M", "1.3G".</summary>
    public static string Compact(long b)
    {
        static string N(double v) => v < 10 ? v.ToString("0.#") : v.ToString("0");
        return b switch
        {
            >= 1L << 30 => N(b / 1073741824.0) + "G",
            >= 1 << 20 => N(b / 1048576.0) + "M",
            >= 1 << 10 => N(b / 1024.0) + "K",
            0 => "0",
            _ => b + "B",
        };
    }
}
