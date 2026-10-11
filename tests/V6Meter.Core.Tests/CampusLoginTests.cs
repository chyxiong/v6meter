using System.Text;
using V6Meter.Core.Campus;

namespace V6Meter.Core.Tests;

public class CampusLoginTests
{
    private static readonly CampusAccount Fake = new("2020000001", "pw");

    [Fact]
    public void BodyMatchesTheLoginForm()
    {
        string body = Encoding.ASCII.GetString(CampusLogin.BuildBody(new("2020000001", "a&b=c 1")));
        Assert.Equal("DDDDD=2020000001&upass=a%26b%3Dc%201&0MKKey=%B5%C7+%C2%BC%A1%A1Login&v6ip=", body);
    }

    [Fact]
    public void AccountNeverPrintsThePassword() =>
        Assert.DoesNotContain("pw", Fake.ToString().Replace("CampusAccount", ""));

    // The message line exactly as both campus gateways returned it for a made-up account (gateway/IP ids zeroed).
    private const string NonexistentAccountPage =
        "<title>合肥工业大学宣城校区</title>\n<script>\n" +
        "Msg=01;time='1234567890';flow='1234567890';fsele=0;fee='1234567890';xsele=0;xip='000.000.000.000.';mac='00-00-00-00-00-00';va=00;vb=00;vc=00;vd=0000;ve=0000;vf=0000;\n" +
        "ipm=\"00000000\";ss1=\"000000000000\";ss2=\"0000\";ss3=\"00000000\";ss4=\"000000000000\";msga='userid error1';                \n" +
        "</script>\n<!--Dr.COMWebLoginID_2.htm-->";

    [Fact]
    public void RealPageForANonexistentAccountStopsAsWrongCredentials()
    {
        var reply = CampusLogin.ParseReply(NonexistentAccountPage);
        Assert.Equal(LoginOutcome.WrongCredentials, reply.Outcome);
        Assert.Contains("userid error1", reply.Message);
    }

    [Theory]
    [InlineData("<!--Dr.COMWebLoginID_3.htm--><title>登录成功窗</title>", LoginOutcome.Accepted)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=15;msga='';", LoginOutcome.Accepted)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=01;time='1';msga='';", LoginOutcome.WrongCredentials)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=01;msga='userid error2';", LoginOutcome.WrongCredentials)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=01;msga='ldap auth error';", LoginOutcome.WrongCredentials)] // unknown text: never retry
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=01;msga='error0';", LoginOutcome.Blocked)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=01;msga='error1';", LoginOutcome.Blocked)] // not allowed on the web, not a bad password
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=03;msga='';", LoginOutcome.Blocked)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=04;msga='';", LoginOutcome.Blocked)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=05;msga='';", LoginOutcome.Blocked)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=11;msga='';", LoginOutcome.Blocked)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=02;msga='';", LoginOutcome.Rejected)]
    [InlineData("<!--Dr.COMWebLoginID_2.htm-->Msg=06;msga='';", LoginOutcome.Rejected)]
    [InlineData("<html>something else</html>", LoginOutcome.Unknown)]
    public void ParsesDrcomReplies(string html, LoginOutcome expected) =>
        Assert.Equal(expected, CampusLogin.ParseReply(html).Outcome);

    [Fact]
    public void OnlyTheCampusPortalCountsAsAGateway()
    {
        // Both gateways' status pages carry this line (logged in or not).
        Assert.True(CampusPortal.IsCampusPortalPage(Encoding.ASCII.GetBytes("portalname='x';\nportalid='AH0303797_000';\n")));
        // A home router or a container that happens to sit at 172.18.x.x must never get the password.
        Assert.False(CampusPortal.IsCampusPortalPage(Encoding.ASCII.GetBytes("<html><title>Router login</title></html>")));
        Assert.False(CampusPortal.IsCampusPortalPage([]));
        Assert.False(CampusPortal.IsCampusPortalPage(Encoding.ASCII.GetBytes("portalid='AH0303797_001';")));
    }

    [Fact]
    public void ProbeIsOnlineOnlyWithTheExactText()
    {
        Assert.True(CampusLogin.InterpretProbe(200, null, "Microsoft Connect Test").Online);
        Assert.False(CampusLogin.InterpretProbe(200, null, "<html>login</html>").Online);
        Assert.False(CampusLogin.InterpretProbe(302, null, "Microsoft Connect Test").Online);
    }

    [Theory]
    [InlineData(302, "http://172.18.2.2/a79.htm", "", "172.18.2.2")]
    [InlineData(200, null, "<script>location.href='http://172.18.3.3/'</script>", "172.18.3.3")]
    [InlineData(302, "http://172.18.3.33/", "", null)] // not one of ours
    [InlineData(302, "http://evil.example/172.18.3.3x", "", null)]
    public void ProbeRedirectNamesOnlyOurGateways(int status, string? location, string body, string? gateway) =>
        Assert.Equal(gateway, CampusLogin.InterpretProbe(status, location, body).RedirectGateway);

    [Fact]
    public void BackoffDoublesUpToFiveMinutesWithJitter()
    {
        Assert.Equal(2, CampusLogin.Backoff(1, 0.5).TotalSeconds, 3);
        Assert.Equal(4, CampusLogin.Backoff(2, 0.5).TotalSeconds, 3);
        Assert.Equal(64, CampusLogin.Backoff(6, 0.5).TotalSeconds, 3);
        Assert.Equal(300, CampusLogin.Backoff(20, 0.5).TotalSeconds, 3);
        Assert.Equal(300, CampusLogin.Backoff(1000, 0.5).TotalSeconds, 3);
        Assert.Equal(1.6, CampusLogin.Backoff(1, 0).TotalSeconds, 3);
        Assert.Equal(2.4, CampusLogin.Backoff(1, 1).TotalSeconds, 3);
    }
}

public class CampusAutoLoginTests
{
    private static readonly CampusAccount Fake = new("2020000001", "pw");

    /// <summary>Scripted network: who is reachable, what each login does, whether we are online.</summary>
    private sealed class FakeNetwork : ICampusNetwork
    {
        public bool Online;
        public string? Redirect;
        public List<string> Reachable = [];
        public Dictionary<string, LoginOutcome> Replies = new();
        /// <summary>Logging in on this gateway makes the probe succeed.</summary>
        public string? WorkingGateway;
        public List<string> Logins = [];

        public Task<ProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ProbeResult(Online, Online ? null : Redirect));
        /// <summary>Gateways whose status page shows us logged in; an accepted login adds its gateway.</summary>
        public HashSet<string> LoggedInOn = [];

        public Task<IReadOnlyList<GatewayState>> CheckGatewaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GatewayState>>(Reachable.Select(g => new GatewayState(g, LoggedInOn.Contains(g))).ToList());

        /// <summary>When set, each login waits for it (or for cancellation) before answering.</summary>
        public Task? Hold;

        public async Task<LoginReply> LoginAsync(string gateway, CampusAccount account, CancellationToken ct)
        {
            Logins.Add(gateway);
            if (Hold != null)
                await Hold.WaitAsync(ct);
            var outcome = Replies.GetValueOrDefault(gateway, LoginOutcome.Accepted);
            if (outcome == LoginOutcome.Accepted)
                LoggedInOn.Add(gateway);
            if (outcome == LoginOutcome.Accepted && gateway == WorkingGateway)
                Online = true;
            return new LoginReply(outcome);
        }
    }

    private static (CampusAutoLogin Auto, FakeNetwork Net, Func<DateTime, DateTime> Advance) Setup()
    {
        var net = new FakeNetwork();
        var now = new DateTime(2026, 9, 1, 8, 0, 0);
        var auto = new CampusAutoLogin(net, () => now, () => 0.5);
        auto.Account = Fake;
        return (auto, net, d => now = d);
    }

    [Fact]
    public async Task EditingTheAccountMidLoginCancelsItAndStartsOver()
    {
        var hold = new TaskCompletionSource();
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3"];
        net.Replies["172.18.3.3"] = LoginOutcome.WrongCredentials;
        net.Hold = hold.Task;

        var step = auto.StepAsync(default);   // stuck in the login with the old (wrong) password
        auto.Account = Fake with { Password = "fixed" };
        Assert.Equal(TimeSpan.Zero, await step); // cancelled: retry at once with the new password
        Assert.NotEqual(AutoLoginState.WrongCredentials, auto.State);
    }

    [Fact]
    public async Task AResultForTheOldAccountIsIgnored()
    {
        // The login does not observe cancellation and answers "wrong password" after the account was edited.
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3"];
        net.Replies["172.18.3.3"] = LoginOutcome.WrongCredentials;
        var hold = new TaskCompletionSource();
        var slow = new UncancellableNetwork(net, hold.Task);
        var auto2 = new CampusAutoLogin(slow, random: () => 0.5) { Account = Fake };

        var step = auto2.StepAsync(default);
        auto2.Account = Fake with { Password = "fixed" };
        hold.SetResult();
        Assert.Equal(TimeSpan.Zero, await step);
        Assert.NotEqual(AutoLoginState.WrongCredentials, auto2.State);
    }

    /// <summary>Wraps a fake so its logins wait for <paramref name="hold"/> and ignore cancellation.</summary>
    private sealed class UncancellableNetwork(FakeNetwork inner, Task hold) : ICampusNetwork
    {
        public Task<ProbeResult> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
        public Task<IReadOnlyList<GatewayState>> CheckGatewaysAsync(CancellationToken ct) => inner.CheckGatewaysAsync(ct);

        public async Task<LoginReply> LoginAsync(string gateway, CampusAccount account, CancellationToken ct)
        {
            await hold;
            return await inner.LoginAsync(gateway, account, CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheButtonAndTheLoopNeverLogInAtTheSameTime()
    {
        var hold = new TaskCompletionSource();
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3"];
        net.WorkingGateway = "172.18.3.3";
        net.Hold = hold.Task;

        var step = auto.StepAsync(default);              // the loop is mid-login
        var button = auto.LoginNowAsync(Fake, default);  // the user clicks meanwhile: queued, not concurrent
        Assert.False(button.IsCompleted);
        hold.SetResult();
        await step;
        Assert.Equal(AttemptResult.AlreadyOnline, (await button).Result); // re-probed after the loop's login
        Assert.Single(net.Logins);
    }

    [Fact]
    public async Task NeverPostsToAGatewayWeAreAlreadyLoggedInOn()
    {
        // Logged in on 3.3 but offline: we are in 2.2's area, so only 2.2 is tried.
        var net = new FakeNetwork { Reachable = ["172.18.3.3", "172.18.2.2"], LoggedInOn = ["172.18.3.3"], WorkingGateway = "172.18.2.2" };
        Assert.Equal(new LoginAttempt(AttemptResult.LoggedIn, "172.18.2.2"), await CampusAutoLogin.AttemptAsync(net, Fake, default));
        Assert.Equal(["172.18.2.2"], net.Logins);
    }

    [Fact]
    public async Task LoggedInEverywhereButOfflineSendsNothing()
    {
        // The nightly cut-off or no balance: logging in again cannot help and may disturb the session.
        var net = new FakeNetwork { Reachable = ["172.18.3.3", "172.18.2.2"], LoggedInOn = ["172.18.3.3", "172.18.2.2"] };
        Assert.Equal(AttemptResult.LoggedInButOffline, (await CampusAutoLogin.AttemptAsync(net, Fake, default)).Result);
        Assert.Empty(net.Logins);
    }

    [Fact]
    public async Task TheRedirectIsTrustedOverEverythingElse()
    {
        var net = new FakeNetwork { Reachable = ["172.18.3.3", "172.18.2.2"], Redirect = "172.18.2.2", WorkingGateway = "172.18.2.2" };
        await CampusAutoLogin.AttemptAsync(net, Fake, default);
        Assert.Equal(["172.18.2.2"], net.Logins); // 3.3 comes first in order but is never touched
    }

    [Fact]
    public async Task AlreadyOnlineDoesNothingAndChecksAgainLater()
    {
        var (auto, net, _) = Setup();
        net.Online = true;
        Assert.Equal(CampusAutoLogin.OnlineCheckInterval, await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.Online, auto.State);
        Assert.Empty(net.Logins);
    }

    [Fact]
    public async Task LogsInOnTheGatewayThePortalRedirectedTo()
    {
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3", "172.18.2.2"];
        net.Redirect = "172.18.2.2";
        net.WorkingGateway = "172.18.2.2";
        string? loggedIn = null;
        auto.LoggedIn += g => loggedIn = g;
        Assert.Equal(CampusAutoLogin.OnlineCheckInterval, await auto.StepAsync(default));
        Assert.Equal(["172.18.2.2"], net.Logins);
        Assert.Equal("172.18.2.2", loggedIn);
        Assert.Equal(AutoLoginState.Online, auto.State);
    }

    [Fact]
    public async Task WithoutARedirectTriesEachGatewayUntilOnline()
    {
        // A 2.2 wireless area: 3.3 answers and even accepts a login, but only 2.2 opens the route.
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3", "172.18.2.2"];
        net.WorkingGateway = "172.18.2.2";
        await auto.StepAsync(default);
        Assert.Equal(["172.18.3.3", "172.18.2.2"], net.Logins);
        Assert.Equal("172.18.2.2", auto.LoggedInVia);
    }

    [Fact]
    public async Task NoGatewayBacksOffThenGivesUpAfterTenMinutesUntilTheNetworkChanges()
    {
        var (auto, net, advance) = Setup();
        var start = new DateTime(2026, 9, 1, 8, 0, 0);
        Assert.Equal(2, (await auto.StepAsync(default))!.Value.TotalSeconds, 3);
        Assert.Equal(4, (await auto.StepAsync(default))!.Value.TotalSeconds, 3);
        Assert.Equal(AutoLoginState.NoCampus, auto.State);

        advance(start + CampusAutoLogin.GiveUpAfter);
        Assert.Null(await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.GaveUp, auto.State);
        Assert.Empty(net.Logins);

        auto.NetworkChanged(); // plugged into campus later
        Assert.Equal(AutoLoginState.Starting, auto.State);
        net.Reachable = ["172.18.3.3"];
        net.WorkingGateway = "172.18.3.3";
        Assert.Equal(CampusAutoLogin.OnlineCheckInterval, await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.Online, auto.State);
    }

    [Fact]
    public async Task WrongPasswordStopsImmediatelyUntilTheAccountIsEdited()
    {
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3", "172.18.2.2"];
        net.Replies["172.18.3.3"] = LoginOutcome.WrongCredentials;
        Assert.Null(await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.WrongCredentials, auto.State);
        Assert.Equal(["172.18.3.3"], net.Logins); // not tried again, not on the other gateway either

        Assert.Null(await auto.StepAsync(default));
        Assert.Single(net.Logins);

        auto.NetworkChanged(); // a network change does not retry a wrong password
        Assert.Null(await auto.StepAsync(default));
        Assert.Single(net.Logins);

        auto.Account = Fake with { Password = "fixed" };
        net.Replies.Clear();
        net.WorkingGateway = "172.18.3.3";
        await auto.StepAsync(default);
        Assert.Equal(AutoLoginState.Online, auto.State);
    }

    [Fact]
    public async Task FailedLoginsBackOffAndAreNotCappedByTheNoCampusWindow()
    {
        // Gateway answers but the login never opens the route (e.g. the nightly cut-off): keep retrying.
        var (auto, net, advance) = Setup();
        net.Reachable = ["172.18.3.3"];
        var start = new DateTime(2026, 9, 1, 8, 0, 0);
        Assert.Equal(2, (await auto.StepAsync(default))!.Value.TotalSeconds, 3);
        Assert.Equal(4, (await auto.StepAsync(default))!.Value.TotalSeconds, 3);
        advance(start + TimeSpan.FromHours(1));
        Assert.NotNull(await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.Retrying, auto.State);
        // Once the gateway shows us logged in, the retries only check; the login is not sent again.
        Assert.Single(net.Logins);
    }

    [Fact]
    public async Task BlockedAccountRetriesOnlyAtTheLongestInterval()
    {
        var (auto, net, _) = Setup();
        net.Reachable = ["172.18.3.3"];
        net.Replies["172.18.3.3"] = LoginOutcome.Blocked;
        Assert.Equal(CampusLogin.MaxRetry, await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.Blocked, auto.State);
    }

    [Fact]
    public async Task OneShotAttemptReportsEachOutcome()
    {
        var net = new FakeNetwork { Online = true };
        Assert.Equal(AttemptResult.AlreadyOnline, (await CampusAutoLogin.AttemptAsync(net, Fake, default)).Result);
        Assert.Empty(net.Logins);

        net = new FakeNetwork();
        Assert.Equal(AttemptResult.NoCampus, (await CampusAutoLogin.AttemptAsync(net, Fake, default)).Result);

        net = new FakeNetwork { Reachable = ["172.18.3.3"], WorkingGateway = "172.18.3.3" };
        Assert.Equal(new LoginAttempt(AttemptResult.LoggedIn, "172.18.3.3"), await CampusAutoLogin.AttemptAsync(net, Fake, default));

        net = new FakeNetwork { Reachable = ["172.18.3.3"] };
        Assert.Equal(AttemptResult.Failed, (await CampusAutoLogin.AttemptAsync(net, Fake, default)).Result);

        net = new FakeNetwork { Reachable = ["172.18.3.3"] };
        net.Replies["172.18.3.3"] = LoginOutcome.WrongCredentials;
        Assert.Equal(AttemptResult.WrongCredentials, (await CampusAutoLogin.AttemptAsync(net, Fake, default)).Result);
    }

    [Fact]
    public async Task NoAccountMeansOff()
    {
        var (auto, net, _) = Setup();
        auto.Account = null;
        Assert.Null(await auto.StepAsync(default));
        Assert.Equal(AutoLoginState.Off, auto.State);
        Assert.Empty(net.Logins);
    }
}
