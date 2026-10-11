using System.Text.RegularExpressions;

namespace V6Meter.Core.Campus;

/// <param name="ReportedFlowBytes">Used traffic exactly as shown by the campus self-service page.</param>
public sealed record CampusStatus(int Minutes, long ReportedFlowBytes, decimal BalanceYuan)
{
    /// <summary>
    /// The campus system shows half of the real usage (e.g. it shows 285.922 MB for 571.844 MB used, and a
    /// 200 GB monthly limit is exceeded once it shows 100 GB). This is the real usage, comparable with
    /// the locally measured bytes and with monthly_v4_quota_gb.
    /// </summary>
    public long ActualFlowBytes => ReportedFlowBytes * CampusPortal.ReportedFlowFactor;
}

/// <summary>
/// Reads the campus self-service status page. Queried at startup (with a couple of retries), hourly, after a
/// network change while there is no figure, and when the user clicks the button; always bypasses any
/// system/environment proxy since the page is only reachable on campus.
/// </summary>
public static partial class CampusPortal
{
    /// <summary>
    /// The campus Dr.COM gateways. Most of the campus logs in through 172.18.3.3, some wireless areas through
    /// 172.18.2.2. Login state is per gateway: a client logged in on one sees the other's login page.
    /// Credentials are only ever sent to these addresses.
    /// </summary>
    public static readonly IReadOnlyList<string> Gateways = ["172.18.3.3", "172.18.2.2"];

    public static string StatusUrl(string gateway) => $"http://{gateway}/1";

    /// <summary>
    /// The campus Dr.COM portal id, present on every page both gateways serve (status page logged in or not).
    /// 172.18.x.x is private address space that other networks (home routers, Docker, WSL) also use, so a
    /// gateway only counts as ours if its page carries this id; credentials are never sent otherwise.
    /// </summary>
    public const string PortalId = "AH0303797_000";

    /// <summary>True if the page (raw bytes, GBK; the id is ASCII) comes from the campus portal.</summary>
    public static bool IsCampusPortalPage(ReadOnlySpan<byte> page) =>
        System.Text.Encoding.Latin1.GetString(page).Contains($"portalid='{PortalId}'", StringComparison.Ordinal);

    /// <summary>Real usage = reported flow × this factor (see <see cref="CampusStatus.ActualFlowBytes"/>).</summary>
    public const int ReportedFlowFactor = 2;

    public const string HolidayNote = "学校计费系统在节假日及寒暑假不计流量。";

    [GeneratedRegex(@"time='(\d+)\s*';flow='(\d+)\s*';.*?fee='(\d+)", RegexOptions.Singleline)]
    private static partial Regex StatusPattern();

    /// <summary>Parses the page; flow is in KiB and fee in 1/10000 yuan (see CLAUDE.md).</summary>
    public static CampusStatus? Parse(string html)
    {
        var m = StatusPattern().Match(html);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, out int minutes)
            || !long.TryParse(m.Groups[2].Value, out long flowKiB) || flowKiB > long.MaxValue / 1024 / ReportedFlowFactor
            || !decimal.TryParse(m.Groups[3].Value, out decimal fee))
            return null;
        return new CampusStatus(minutes, flowKiB * 1024, fee / 10000m);
    }

    /// <summary>
    /// Parses raw page bytes. The page is served as GBK, which .NET does not ship by default; the
    /// fields we need are ASCII, so decode as Latin-1 (byte-transparent) instead of honoring the charset.
    /// </summary>
    public static CampusStatus? Parse(ReadOnlySpan<byte> body) => Parse(System.Text.Encoding.Latin1.GetString(body));

    /// <summary>
    /// Asks every gateway (in parallel) and returns the usage from the one this computer is logged in on.
    /// Throws <see cref="InvalidDataException"/> if a gateway answered but none had usage (not logged in), and
    /// the network error if none answered.
    /// </summary>
    public static async Task<CampusStatus> QueryAsync(CancellationToken ct = default)
    {
        using var http = CampusHttp.Create(TimeSpan.FromSeconds(5));
        var attempts = Gateways.Select(async g =>
        {
            try { return (Status: Parse(await http.GetByteArrayAsync(StatusUrl(g), ct).ConfigureAwait(false)), Error: (Exception?)null); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, ex); }
        }).ToList();
        var results = await Task.WhenAll(attempts).ConfigureAwait(false);
        if (results.FirstOrDefault(r => r.Status != null).Status is { } status)
            return status;
        if (results.Any(r => r.Error == null))
            throw new InvalidDataException("页面中没有找到流量信息，可能尚未登录校园网");
        ct.ThrowIfCancellationRequested();
        throw results[0].Error!;
    }
}
