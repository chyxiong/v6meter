namespace V6Meter.Core.Campus;

public enum AttemptResult
{
    /// <summary>IPv4 internet already worked; nothing was sent.</summary>
    AlreadyOnline,
    LoggedIn,
    /// <summary>Neither gateway answers: not connected to the campus network.</summary>
    NoCampus,
    WrongCredentials,
    Blocked,
    /// <summary>Logged in (or tried) but still offline.</summary>
    Failed,
    /// <summary>
    /// Every reachable gateway already shows us logged in, yet there is no internet (the nightly cut-off, no
    /// balance): nothing was sent, since posting a login to a gateway we are on may disturb that session.
    /// </summary>
    LoggedInButOffline,
}

/// <param name="Gateway">The gateway that got us online, for <see cref="AttemptResult.LoggedIn"/>.</param>
public sealed record LoginAttempt(AttemptResult Result, string? Gateway = null, string? Message = null);

public enum AutoLoginState
{
    /// <summary>No account saved, or the feature is off.</summary>
    Off,
    /// <summary>Started, nothing checked yet.</summary>
    Starting,
    /// <summary>IPv4 internet works (logged in, by us or anyone else).</summary>
    Online,
    /// <summary>Neither campus gateway answers: no network yet, or not on campus.</summary>
    NoCampus,
    /// <summary>Not on campus for a while: stopped until the network changes.</summary>
    GaveUp,
    /// <summary>On campus but the last login attempt did not get us online; retrying with backoff.</summary>
    Retrying,
    /// <summary>Account suspended / over its limit: retried only at the longest interval.</summary>
    Blocked,
    /// <summary>Wrong account or password: stopped until the account is edited.</summary>
    WrongCredentials,
}

/// <summary>
/// Keeps this computer logged in to the campus network. Each <see cref="StepAsync"/> checks once and says
/// how long to wait before the next check; <see cref="RunAsync"/> loops over it and wakes early on a network
/// change. The rules:
/// - "Online" is decided by an IPv4 connectivity probe, not by a gateway's status page: login state is per
///   gateway, so a page without usage data does not mean "not logged in".
/// - Log in on the gateway the probe was redirected to; without a redirect, only on gateways we are not
///   already logged in on (see <see cref="AttemptAsync"/>).
/// - No gateway reachable: back off (2 s … 5 min), and give up after 10 minutes until the network changes.
/// - Wrong password: stop at once (repeated failures could lock the account) until the account is edited.
/// - Online: check again every 5 minutes (the campus logs everyone out at night).
/// </summary>
public sealed class CampusAutoLogin
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OnlineCheckInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(3);

    private readonly ICampusNetwork _net;
    private readonly Func<DateTime> _now;
    private readonly Func<double> _random;
    private readonly object _lock = new();
    private CampusAccount? _account;
    private DateTime _windowStart;
    private int _failures;
    private SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _pendingWake;

    // One check-and-login at a time, whether from the loop or the button: two concurrent ones could both see
    // "offline" and both post, the second to a gateway the first just logged in on.
    private readonly SemaphoreSlim _attemptGate = new(1, 1);

    // Bumped on every account change; an attempt started under an older version is cancelled and its result ignored.
    private int _accountVersion;
    private CancellationTokenSource _accountCts = new();

    public CampusAutoLogin(ICampusNetwork net, Func<DateTime>? now = null, Func<double>? random = null)
    {
        _net = net;
        _now = now ?? (() => DateTime.UtcNow);
        _random = random ?? Random.Shared.NextDouble;
        _windowStart = _now();
    }

    public AutoLoginState State { get; private set; } = AutoLoginState.Off;

    /// <summary>Shown to the user for the failure states (Chinese).</summary>
    public string? Message { get; private set; }

    /// <summary>Gateway the last successful login went through.</summary>
    public string? LoggedInVia { get; private set; }

    /// <summary>Raised (on the loop's thread) whenever <see cref="State"/> or <see cref="Message"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised (on the loop's thread) after this class logged the computer in.</summary>
    public event Action<string>? LoggedIn;

    /// <summary>The account to log in with; null turns the feature off. Setting it starts over at once.</summary>
    public CampusAccount? Account
    {
        get { lock (_lock) return _account; }
        set
        {
            CancellationTokenSource old;
            lock (_lock)
            {
                _account = value;
                _failures = 0;
                _windowStart = _now();
                _accountVersion++;
                old = _accountCts;
                _accountCts = new CancellationTokenSource();
            }
            old.Cancel(); // a login still running with the previous account (or after turning off) stops here
            old.Dispose();
            if (value == null)
                Set(AutoLoginState.Off, null);
            else if (State is AutoLoginState.Off or AutoLoginState.WrongCredentials or AutoLoginState.GaveUp)
                Set(AutoLoginState.Starting, null);
            Wake(TimeSpan.Zero);
        }
    }

    /// <summary>Network came up, changed or the computer resumed: start a fresh 10-minute window soon.</summary>
    public void NetworkChanged()
    {
        lock (_lock)
        {
            _failures = 0;
            _windowStart = _now();
        }
        if (State == AutoLoginState.GaveUp)
            Set(AutoLoginState.Starting, null);
        Wake(NetworkSettleDelay); // addresses change in bursts while a link comes up
    }

    /// <summary>One check (and login if needed). Returns the delay before the next one, or null to wait for a change.</summary>
    public async Task<TimeSpan?> StepAsync(CancellationToken ct)
    {
        CampusAccount? account;
        int version;
        CancellationToken accountToken;
        lock (_lock)
            (account, version, accountToken) = (_account, _accountVersion, _accountCts.Token);
        if (account == null)
        {
            Set(AutoLoginState.Off, null);
            return null;
        }
        if (State == AutoLoginState.WrongCredentials)
            return null;

        LoginAttempt attempt;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, accountToken);
            attempt = await ExclusiveAttemptAsync(account, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimeSpan.Zero; // the account changed mid-attempt: start over with the new one
        }
        lock (_lock)
            if (version != _accountVersion)
                return TimeSpan.Zero; // finished just as the account changed: its result is about the old one

        switch (attempt.Result)
        {
            case AttemptResult.AlreadyOnline:
                return Online();
            case AttemptResult.LoggedIn:
                LoggedInVia = attempt.Gateway;
                var next = Online();
                LoggedIn?.Invoke(attempt.Gateway!);
                return next;
            case AttemptResult.NoCampus:
                if (_now() - _windowStart >= GiveUpAfter)
                {
                    Set(AutoLoginState.GaveUp, "超过 10 分钟没连上校园网，暂停自动登录；网络变化时会再试");
                    return null;
                }
                Set(AutoLoginState.NoCampus, attempt.Message);
                return NextBackoff();
            case AttemptResult.WrongCredentials:
                Set(AutoLoginState.WrongCredentials, attempt.Message);
                return null;
            case AttemptResult.Blocked:
                Set(AutoLoginState.Blocked, attempt.Message);
                return CampusLogin.MaxRetry;
            default:
                Set(AutoLoginState.Retrying, attempt.Message);
                return NextBackoff();
        }
    }

    /// <summary>
    /// The "登录校园网" button: one check-and-login now, queued behind any attempt the loop is making (which
    /// re-probes first, so a login the loop just made is seen as <see cref="AttemptResult.AlreadyOnline"/>).
    /// </summary>
    public Task<LoginAttempt> LoginNowAsync(CampusAccount account, CancellationToken ct) => ExclusiveAttemptAsync(account, ct);

    private async Task<LoginAttempt> ExclusiveAttemptAsync(CampusAccount account, CancellationToken ct)
    {
        await _attemptGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await AttemptAsync(_net, account, ct).ConfigureAwait(false); }
        finally { _attemptGate.Release(); }
    }

    /// <summary>
    /// One check-and-login, shared by the auto-login loop and the "登录校园网" button. Which gateway to log in on:
    /// 1. The gateway the probe was redirected to. When not logged in, the campus network redirects any web
    ///    request to the gateway in charge of the current network, so that one alone is used.
    /// 2. Without a recognisable redirect: the reachable gateways we are NOT already logged in on (their status
    ///    pages are read first; posting a login to a gateway we are on may disturb that session). Logged in on
    ///    3.3 yet offline means this area belongs to 2.2, so only 2.2 is tried. If we are logged in on every
    ///    reachable gateway, nothing is sent (<see cref="AttemptResult.LoggedInButOffline"/>).
    /// After each login the probe is repeated, and the first gateway that gets us online wins. A wrong password stops at once.
    /// </summary>
    public static async Task<LoginAttempt> AttemptAsync(ICampusNetwork net, CampusAccount account, CancellationToken ct)
    {
        var probe = await net.ProbeAsync(ct).ConfigureAwait(false);
        if (probe.Online)
            return new(AttemptResult.AlreadyOnline);

        var gateways = await net.CheckGatewaysAsync(ct).ConfigureAwait(false);
        if (gateways.Count == 0)
            return new(AttemptResult.NoCampus, Message: "还没连上校园网");

        IReadOnlyList<string> targets;
        if (probe.RedirectGateway is { } redirect && gateways.Any(s => s.Gateway == redirect))
        {
            targets = [redirect];
        }
        else
        {
            targets = gateways.Where(s => !s.LoggedIn).Select(s => s.Gateway).ToList();
            if (targets.Count == 0)
                return new(AttemptResult.LoggedInButOffline, Message: "已经登录，但还是上不了外网（可能是断网时段或欠费）");
        }
        LoginReply? blocked = null, last = null;
        foreach (var gateway in targets)
        {
            var reply = await net.LoginAsync(gateway, account, ct).ConfigureAwait(false);
            if (reply.Outcome == LoginOutcome.WrongCredentials)
                return new(AttemptResult.WrongCredentials, gateway, reply.Message ?? "账号或密码不对");
            last = reply;
            if (reply.Outcome == LoginOutcome.Blocked)
                blocked = reply;
            if ((await net.ProbeAsync(ct).ConfigureAwait(false)).Online)
                return new(AttemptResult.LoggedIn, gateway);
        }
        if (blocked != null)
            return new(AttemptResult.Blocked, Message: blocked.Message);
        return new(AttemptResult.Failed, Message: last?.Message is { Length: > 0 } m ? m : "登录后仍然上不了网");
    }

    /// <summary>Runs until cancelled. Exceptions from one step count as a failed attempt.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan? next;
            try
            {
                next = await StepAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceError($"v6meter auto-login step failed: {ex}");
                next = NextBackoff();
            }
            await WaitAsync(next, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan Online()
    {
        lock (_lock)
            _failures = 0;
        Set(AutoLoginState.Online, null);
        return OnlineCheckInterval;
    }

    private TimeSpan NextBackoff()
    {
        int failures;
        lock (_lock)
            failures = ++_failures;
        return CampusLogin.Backoff(failures, _random());
    }

    private async Task WaitAsync(TimeSpan? delay, CancellationToken ct)
    {
        SemaphoreSlim wake;
        lock (_lock)
            wake = _wake;
        if (delay is { } d)
            await wake.WaitAsync(d, ct).ConfigureAwait(false);
        else
            await wake.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
            _wake = new SemaphoreSlim(0); // drop extra wake-ups that piled up while we waited
    }

    /// <summary>Ends the current wait after <paramref name="after"/>; a newer call replaces an older pending one.</summary>
    private void Wake(TimeSpan after)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pendingWake?.Cancel();
            _pendingWake = cts = new CancellationTokenSource();
        }
        if (after <= TimeSpan.Zero)
        {
            Release();
            return;
        }
        _ = Task.Delay(after, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Release(); }, TaskScheduler.Default);
    }

    private void Release()
    {
        lock (_lock)
            if (_wake.CurrentCount == 0)
                _wake.Release();
    }

    private void Set(AutoLoginState state, string? message)
    {
        if (State == state && Message == message)
            return;
        State = state;
        Message = message;
        Changed?.Invoke();
    }
}
