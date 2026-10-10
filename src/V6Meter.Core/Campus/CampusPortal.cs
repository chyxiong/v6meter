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
    public const string StatusUrl = "http://172.18.3.3/1";

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

    public static async Task<CampusStatus> QueryAsync(CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        byte[] body = await http.GetByteArrayAsync(StatusUrl, ct).ConfigureAwait(false);
        return Parse(body) ?? throw new InvalidDataException("页面中没有找到流量信息，可能尚未登录校园网");
    }
}
