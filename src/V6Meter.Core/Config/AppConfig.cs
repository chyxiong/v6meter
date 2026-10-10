using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using V6Meter.Core.Classification;

namespace V6Meter.Core.Config;

/// <summary>config.json next to the exe. Property names are snake_case on disk.</summary>
public sealed record AppConfig
{
    public List<string> ExemptV4Cidrs { get; init; } = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];
    public List<string> Nat64Prefixes { get; init; } = ["64:ff9b::/96"];
    public bool Nat64IsBilled { get; init; } = true;
    /// <summary>The campus limit is 200 GB a month (and 30 GB a day); 0 hides the quota bar.</summary>
    public double MonthlyV4QuotaGb { get; init; } = 200;
    public bool BillingCountsUpload { get; init; } = false;
    public double AlertThresholdMbPerSec { get; init; } = 5.0;
    public int AlertSustainSeconds { get; init; } = 30;
    public int AlertCooldownMinutes { get; init; } = 5;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>
    /// Loads the config and never throws. All defaults are built in, so the program runs without any file:
    /// a missing file yields defaults (and a default file is written if the folder is writable).
    /// Returns false, with built-in defaults in <paramref name="config"/>, if the file exists but cannot be
    /// read or parsed; callers that already have a working config should keep it instead.
    /// </summary>
    public static bool TryLoad(string path, out AppConfig config, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            config = new AppConfig();
            // In user mode the exe may sit in a folder we cannot write to; defaults still work.
            try { File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions)); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            return true;
        }
        try
        {
            config = Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            config = new AppConfig();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Parses JSON; missing fields keep their defaults and explicit nulls fall back to them too.</summary>
    public static AppConfig Parse(string json)
    {
        var c = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        var d = new AppConfig();
        return c with
        {
            ExemptV4Cidrs = c.ExemptV4Cidrs ?? d.ExemptV4Cidrs,
            Nat64Prefixes = c.Nat64Prefixes ?? d.Nat64Prefixes,
        };
    }

    /// <summary>Builds classifier options; invalid CIDRs are skipped and reported in <paramref name="errors"/>.</summary>
    public ClassifierOptions ToClassifierOptions(out List<string> errors)
    {
        var errs = new List<string>();
        List<IPNetwork> ParseAll(IEnumerable<string?> cidrs, string field)
        {
            var list = new List<IPNetwork>();
            foreach (var s in cidrs)
            {
                if (s == null) // JSON allows [null]
                    errs.Add($"{field}: null entry");
                else if (IPNetwork.TryParse(s.Trim(), out var n))
                    list.Add(n);
                else
                    errs.Add($"{field}: invalid CIDR '{s}'");
            }
            return list;
        }
        var opts = new ClassifierOptions(ParseAll(ExemptV4Cidrs, "exempt_v4_cidrs"), ParseAll(Nat64Prefixes, "nat64_prefixes"));
        errors = errs;
        return opts;
    }
}
