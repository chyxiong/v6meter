using System.Net;
using V6Meter.Core.Config;
using V6Meter.Core.Processes;

namespace V6Meter.Core.Tests;

public class ProcessNamingTests
{
    [Fact]
    public void PrefersFileDescription() =>
        Assert.Equal("百度网盘", ProcessNaming.ForExe(@"D:\BaiduNetdisk\BaiduNetdisk.exe", "百度网盘", null).DisplayName);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToFileName(string? description) =>
        Assert.Equal("verge-mihomo.exe", ProcessNaming.ForExe(@"C:\Clash\verge-mihomo.exe", description, null).DisplayName);

    [Fact]
    public void KeyIsCaseInsensitivePath() =>
        Assert.Equal(
            ProcessNaming.ForExe(@"C:\Windows\System32\curl.exe", "The curl executable", null).Key,
            ProcessNaming.ForExe(@"c:\windows\system32\CURL.EXE", "The curl executable", null).Key);

    [Fact]
    public void SvchostShowsSingleHostedService()
    {
        var id = ProcessNaming.ForExe(@"C:\Windows\System32\svchost.exe", "Windows 服务主进程", [new ServiceEntry("DoSvc", "传递优化")]);
        Assert.Equal("传递优化 (DoSvc)", id.DisplayName);
        Assert.Equal(@"c:\windows\system32\svchost.exe|dosvc", id.Key);
    }

    [Fact]
    public void SvchostWithSeveralServicesHasStableKey()
    {
        var a = ProcessNaming.ForExe(@"C:\Windows\System32\svchost.exe", null, [new ServiceEntry("b", "B"), new ServiceEntry("A", "A")]);
        var b = ProcessNaming.ForExe(@"C:\Windows\System32\svchost.exe", null, [new ServiceEntry("A", "A"), new ServiceEntry("b", "B")]);
        Assert.Equal(a.Key, b.Key);
        Assert.Equal("svchost (A, b)", a.DisplayName);
    }

    [Fact]
    public void ServicesOnNonSvchostAreIgnored() =>
        Assert.Equal(@"c:\x\agent.exe", ProcessNaming.ForExe(@"C:\x\agent.exe", "Agent", [new ServiceEntry("AgentSvc", "Agent")]).Key);

    [Fact]
    public void SvchostWithoutServiceInfoKeepsPlainKey() =>
        Assert.Equal(@"c:\windows\system32\svchost.exe", ProcessNaming.ForExe(@"C:\Windows\System32\svchost.exe", null, []).Key);

    [Fact]
    public void UnknownAndSystem()
    {
        Assert.Equal("pid:42", ProcessNaming.Unknown(42).Key);
        Assert.Equal(ProcessNaming.SystemKey, ProcessNaming.System.Key);
    }

    private static readonly Dictionary<string, string> Drives = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\Device\HarddiskVolume3"] = "C:",
        [@"\Device\HarddiskVolume13"] = "F:",
    };

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\curl.exe", @"C:\Windows\System32\curl.exe")]
    [InlineData(@"\Device\HarddiskVolume13\Projects\a.exe", @"F:\Projects\a.exe")] // must not match Volume1/Volume3 by prefix
    [InlineData(@"\device\harddiskvolume3\x.exe", @"C:\x.exe")]
    [InlineData(@"C:\already\dos.exe", @"C:\already\dos.exe")]
    public void DevicePathConversion(string nt, string expected) =>
        Assert.Equal(expected, DevicePaths.TryConvert(nt, Drives));

    [Fact]
    public void UnknownDeviceReturnsNull() =>
        Assert.Null(DevicePaths.TryConvert(@"\Device\Mup\server\share\a.exe", Drives));
}

public class AppConfigTests
{
    [Fact]
    public void DefaultsMatchConfigExample()
    {
        var example = File.ReadAllText(Path.Combine(FindRepoRoot(), "config.example.json"));
        string Json(AppConfig c) => System.Text.Json.JsonSerializer.Serialize(c, AppConfig.JsonOptions);
        Assert.Equal(Json(new AppConfig()), Json(AppConfig.Parse(example)));
    }

    [Fact]
    public void ParsesSnakeCaseAndKeepsDefaultsForMissingFields()
    {
        var c = AppConfig.Parse("""{ "exempt_v4_cidrs": ["202.120.0.0/16"], "monthly_v4_quota_gb": 50, }""");
        Assert.Equal(["202.120.0.0/16"], c.ExemptV4Cidrs);
        Assert.Equal(50, c.MonthlyV4QuotaGb);
        Assert.True(c.Nat64IsBilled);
        Assert.Equal(30, c.AlertSustainSeconds);
    }

    [Fact]
    public void FieldsFromOlderVersionsAreIgnored()
    {
        var c = AppConfig.Parse("""{ "billing_cycle_start_day": 15, "payload_overhead_factor": 1.05, "monthly_v4_quota_gb": 300 }""");
        Assert.Equal(300, c.MonthlyV4QuotaGb);
    }

    [Fact]
    public void InvalidCidrsAreReportedAndSkipped()
    {
        var c = new AppConfig { ExemptV4Cidrs = ["10.0.0.0/8", "not-a-cidr", "300.1.1.1/8"] };
        var opts = c.ToClassifierOptions(out var errors);
        Assert.Equal([IPNetwork.Parse("10.0.0.0/8")], opts.ExemptV4);
        Assert.Equal(2, errors.Count);
        Assert.Contains("not-a-cidr", errors[0]);
    }

    private static string Json(AppConfig c) => System.Text.Json.JsonSerializer.Serialize(c, AppConfig.JsonOptions);

    private static void WithTempDir(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "v6meter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { test(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingFileWritesDefaultsThenReadsThemBack() => WithTempDir(dir =>
    {
        string path = Path.Combine(dir, "config.json");
        Assert.True(AppConfig.TryLoad(path, out var created, out var error));
        Assert.Null(error);
        Assert.True(File.Exists(path));
        Assert.Contains("\"exempt_v4_cidrs\"", File.ReadAllText(path));
        Assert.True(AppConfig.TryLoad(path, out var loaded, out _));
        Assert.Equal(Json(created), Json(loaded));
    });

    [Fact]
    public void MissingFileInUnwritableLocationStillGivesDefaults()
    {
        // A folder that does not exist stands in for a read-only exe folder: the write fails, defaults are used.
        string path = Path.Combine(Path.GetTempPath(), "v6meter-missing-" + Guid.NewGuid().ToString("N"), "config.json");
        Assert.True(AppConfig.TryLoad(path, out var cfg, out var error));
        Assert.Null(error);
        Assert.Equal(Json(new AppConfig()), Json(cfg));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("{ \"exempt_v4_cidrs\": [\"10.0.0.0/8\" ")] // truncated edit
    [InlineData("")]                                        // empty file
    [InlineData("exempt_v4_cidrs = 10.0.0.0/8")]           // not JSON
    [InlineData("{ \"alert_sustain_seconds\": \"ten\" }")] // wrong type
    public void InvalidFileFallsBackToDefaultsWithError(string content) => WithTempDir(dir =>
    {
        string path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, content);
        Assert.False(AppConfig.TryLoad(path, out var cfg, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(Json(new AppConfig()), Json(cfg));
        Assert.Equal(content, File.ReadAllText(path)); // the user's file is never overwritten
    });

    [Fact]
    public void LockedFileFallsBackToDefaultsWithError() => WithTempDir(dir =>
    {
        string path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{}");
        using var lockIt = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(AppConfig.TryLoad(path, out var cfg, out var error));
        Assert.NotNull(error);
        Assert.Equal(Json(new AppConfig()), Json(cfg));
    });

    [Fact]
    public void NullListsFallBackToDefaults()
    {
        var c = AppConfig.Parse("""{ "exempt_v4_cidrs": null, "nat64_prefixes": null }""");
        Assert.Equal(new AppConfig().ExemptV4Cidrs, c.ExemptV4Cidrs);
        Assert.Equal(new AppConfig().Nat64Prefixes, c.Nat64Prefixes);
        c.ToClassifierOptions(out var errors); // must not throw
        Assert.Empty(errors);
    }

    [Fact]
    public void NullListEntriesAreReportedNotThrown()
    {
        var c = AppConfig.Parse("""{ "exempt_v4_cidrs": [null, "10.0.0.0/8"], "nat64_prefixes": [null] }""");
        var opts = c.ToClassifierOptions(out var errors);
        Assert.Equal(2, errors.Count);
        Assert.Single(opts.ExemptV4);
        Assert.Empty(opts.Nat64Prefixes);
    }

    [Fact]
    public void LiteralNullDocumentGivesDefaults() =>
        Assert.Equal(Json(new AppConfig()), Json(AppConfig.Parse("null")));

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "config.example.json")))
            d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
