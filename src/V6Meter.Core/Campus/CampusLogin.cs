using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace V6Meter.Core.Campus;

/// <summary>A campus network account. <see cref="ToString"/> never shows the password.</summary>
public sealed record CampusAccount(string Account, string Password)
{
    public override string ToString() => $"CampusAccount {{ Account = {Account} }}";
}

public enum LoginOutcome
{
    /// <summary>The gateway showed its "logged in" page.</summary>
    Accepted,
    /// <summary>Wrong account or password: retrying cannot help, stop until the user edits them.</summary>
    WrongCredentials,
    /// <summary>Account suspended, over its limit or out of balance: retrying soon will not help.</summary>
    Blocked,
    /// <summary>Refused for another reason (e.g. logged in elsewhere); worth retrying later.</summary>
    Rejected,
    /// <summary>A page we do not recognise.</summary>
    Unknown,
}

public sealed record LoginReply(LoginOutcome Outcome, string? Message = null);

/// <summary>Result of the IPv4 connectivity probe.</summary>
/// <param name="RedirectGateway">The campus gateway that intercepted the probe, if it named one of ours.</param>
public sealed record ProbeResult(bool Online, string? RedirectGateway = null)
{
    public static ProbeResult Offline { get; } = new(false);
}

/// <summary>
/// Dr.COM web login (the form on the campus login page) and the checks around it. The pure parts are here
/// so they can be tested; <see cref="CampusNetwork"/> does the HTTP.
/// </summary>
public static partial class CampusLogin
{
    /// <summary>Windows' own connectivity check URL; over IPv4 it returns exactly <see cref="ProbeExpected"/>.</summary>
    public const string ProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";
    public const string ProbeExpected = "Microsoft Connect Test";

    public static string LoginUrl(string gateway) => $"http://{gateway}/0.htm";

    /// <summary>
    /// The login form body. 0MKKey is the submit button's GBK-encoded caption ("登 录　Login"), sent exactly as
    /// the login page sends it.
    /// </summary>
    public static byte[] BuildBody(CampusAccount a) => Encoding.ASCII.GetBytes(
        $"DDDDD={Uri.EscapeDataString(a.Account)}&upass={Uri.EscapeDataString(a.Password)}&0MKKey=%B5%C7+%C2%BC%A1%A1Login&v6ip=");

    [GeneratedRegex(@"\bMsg=(\d+)")]
    private static partial Regex MsgPattern();

    [GeneratedRegex(@"msga='([^']*)'")]
    private static partial Regex MsgaPattern();

    /// <summary>
    /// Reads the page returned by a login POST. Dr.COM pages name themselves in a comment: _3 is "logged in",
    /// _2 is the message page, whose script (DispTFM, captured from both campus gateways with a made-up
    /// account) maps Msg/msga to text:
    /// - Msg 0/1: msga 'error0' = this IP may not log in on the web, 'error1' = this account may not,
    ///   'error2' = may not change password, any other msga is shown as is (a nonexistent account gives
    ///   'userid error1'), and an empty msga means "账号或密码不对".
    /// - 2 in use elsewhere, 3/11 only from an appointed address/MAC, 4 over limit or no balance,
    ///   5 suspended, 6 gateway buffer full, 15 logged in.
    /// Every Msg 0/1 failure other than error0/1/2 counts as wrong credentials and stops retries: wrong
    /// passwords are the case to never repeat, and an unknown text there is most likely one of them.
    /// </summary>
    public static LoginReply ParseReply(string html)
    {
        if (html.Contains("Dr.COMWebLoginID_3.htm", StringComparison.OrdinalIgnoreCase))
            return new(LoginOutcome.Accepted);
        var msg = MsgPattern().Match(html);
        if (!msg.Success)
            return new(LoginOutcome.Unknown);
        string msga = MsgaPattern().Match(html) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        return int.Parse(msg.Groups[1].Value) switch
        {
            15 => new(LoginOutcome.Accepted),
            0 or 1 => msga switch
            {
                "error0" => new(LoginOutcome.Blocked, "本机 IP 不允许网页登录"),
                "error1" => new(LoginOutcome.Blocked, "本账号不允许网页登录"),
                "error2" => new(LoginOutcome.Rejected, "本账号不允许修改密码"),
                "" => new(LoginOutcome.WrongCredentials, "账号或密码不对"),
                _ when msga.StartsWith("userid error", StringComparison.OrdinalIgnoreCase) =>
                    new(LoginOutcome.WrongCredentials, "账号或密码不对（学校返回 " + msga + "）"),
                _ => new(LoginOutcome.WrongCredentials, "登录被拒绝：" + msga),
            },
            2 => new(LoginOutcome.Rejected, "该账号正在别处使用"),
            3 or 11 => new(LoginOutcome.Blocked, "本账号只能在指定的地址使用"),
            4 => new(LoginOutcome.Blocked, "账号余额不足或流量、时长超限"),
            5 => new(LoginOutcome.Blocked, "账号已暂停使用"),
            6 => new(LoginOutcome.Rejected, "网关繁忙"),
            var code => new(LoginOutcome.Rejected, msga.Length > 0 ? msga : $"网关拒绝登录（代码 {code}）"),
        };
    }

    /// <summary>
    /// Online only if the probe returned Windows' exact text. Anything else is a captive portal or no network;
    /// a portal that names one of our gateways (in the redirect or the page) tells us where to log in.
    /// </summary>
    public static ProbeResult InterpretProbe(int statusCode, string? location, string body)
    {
        if (statusCode == 200 && body.Trim() == ProbeExpected)
            return new(true);
        string text = (location ?? "") + "\n" + body;
        foreach (var g in CampusPortal.Gateways)
            if (Regex.IsMatch(text, $@"//{Regex.Escape(g)}(?=[/:'""?#\s]|$)")) // the host of a URL, not any mention
                return new(false, g);
        return ProbeResult.Offline;
    }

    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Exponential backoff: 2, 4, 8 … seconds, capped at 5 minutes, with ±20 % jitter so a building full of
    /// computers starting together does not hit the gateway in the same second.
    /// </summary>
    /// <param name="failures">Consecutive failures so far (1 for the first).</param>
    /// <param name="random01">A random number in [0, 1).</param>
    public static TimeSpan Backoff(int failures, double random01)
    {
        double seconds = FirstRetry.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        seconds = Math.Min(seconds, MaxRetry.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * random01));
    }
}

/// <param name="LoggedIn">This computer is logged in on this gateway (its status page shows usage).</param>
public sealed record GatewayState(string Gateway, bool LoggedIn);

/// <summary>What the auto-login needs from the network; faked in tests.</summary>
public interface ICampusNetwork
{
    Task<ProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// The gateways that answer with the campus portal (<see cref="CampusPortal.IsCampusPortalPage"/>), in
    /// <see cref="CampusPortal.Gateways"/> order, each with whether this computer is logged in on it (its
    /// status page shows usage). Only these ever receive a login.
    /// </summary>
    Task<IReadOnlyList<GatewayState>> CheckGatewaysAsync(CancellationToken ct);

    Task<LoginReply> LoginAsync(string gateway, CampusAccount account, CancellationToken ct);
}

public sealed class CampusNetwork : ICampusNetwork, IDisposable
{
    private readonly HttpClient _http = CampusHttp.Create(TimeSpan.FromSeconds(5));
    private readonly HttpClient _quick = CampusHttp.Create(TimeSpan.FromSeconds(3));

    static CampusNetwork() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // GBK login pages

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(CampusLogin.ProbeUrl, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return CampusLogin.InterpretProbe((int)resp.StatusCode, resp.Headers.Location?.OriginalString, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ProbeResult.Offline; // no route, DNS failure or timeout
        }
    }

    public async Task<IReadOnlyList<GatewayState>> CheckGatewaysAsync(CancellationToken ct)
    {
        var checks = CampusPortal.Gateways.Select(async g =>
        {
            try
            {
                byte[] page = await _quick.GetByteArrayAsync(CampusPortal.StatusUrl(g), ct).ConfigureAwait(false);
                // Something answered at a campus address; it is only our gateway if the page is the campus portal.
                return CampusPortal.IsCampusPortalPage(page) ? new GatewayState(g, LoggedIn: CampusPortal.Parse(page) != null) : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }).ToList();
        var states = await Task.WhenAll(checks).ConfigureAwait(false);
        return states.OfType<GatewayState>().ToList();
    }

    public async Task<LoginReply> LoginAsync(string gateway, CampusAccount account, CancellationToken ct)
    {
        var content = new ByteArrayContent(CampusLogin.BuildBody(account));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        try
        {
            using var resp = await _http.PostAsync(CampusLogin.LoginUrl(gateway), content, ct).ConfigureAwait(false);
            byte[] body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var reply = CampusLogin.ParseReply(Encoding.GetEncoding(936).GetString(body));
            // The gateway opens the route a moment after answering; give it a second before the caller re-probes.
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            return reply;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(LoginOutcome.Unknown, ex.Message);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _quick.Dispose();
    }
}
