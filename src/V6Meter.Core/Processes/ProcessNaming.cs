namespace V6Meter.Core.Processes;

/// <summary>Identity used for aggregation: one row per exe (per hosted-service set for svchost).</summary>
public sealed record ProcessIdentity(string Key, string DisplayName, string? ExePath);

public sealed record ServiceEntry(string Name, string DisplayName);

public static class ProcessNaming
{
    public const string SystemKey = "system";

    public static ProcessIdentity System { get; } = new(SystemKey, "System", null);

    public static ProcessIdentity Unknown(uint pid) => new($"pid:{pid}", $"PID {pid}", null);

    /// <summary>Display name: FileDescription when present (gives localized names), else the exe file name.</summary>
    public static string DisplayName(string exePath, string? fileDescription) =>
        string.IsNullOrWhiteSpace(fileDescription) ? Path.GetFileName(exePath) : fileDescription.Trim();

    public static bool IsSvchost(string exePath) =>
        Path.GetFileName(exePath).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);

    public static ProcessIdentity ForExe(string exePath, string? fileDescription, IReadOnlyList<ServiceEntry>? services)
    {
        string key = exePath.ToLowerInvariant();
        if (services is { Count: > 0 } && IsSvchost(exePath))
        {
            var sorted = services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            key += "|" + string.Join(",", sorted.Select(s => s.Name.ToLowerInvariant()));
            string name = sorted.Count == 1
                ? $"{sorted[0].DisplayName} ({sorted[0].Name})"
                : $"svchost ({string.Join(", ", sorted.Select(s => s.Name))})";
            return new ProcessIdentity(key, name, exePath);
        }
        return new ProcessIdentity(key, DisplayName(exePath, fileDescription), exePath);
    }
}
