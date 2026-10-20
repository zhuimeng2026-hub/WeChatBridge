using System.Text.Json;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A stand-in for the licence service, and for time.
/// <para>
/// It records every request it is handed and answers whatever the test scripted.
/// There is no retry in here either, deliberately: a fake that retried would make
/// <see cref="AccountService"/>'s own "it never retries on its own" property
/// untestable, because the retries would come from the harness instead.
/// </para>
/// </summary>
internal sealed class FakeActivationTransport : IActivationTransport
{
    private readonly Queue<Func<ApiRequest, CancellationToken, Task<ApiReply>>> _script = new();
    private readonly List<ApiRequest> _requests = [];
    private readonly List<string> _fetches = [];
    private Func<ApiRequest, CancellationToken, Task<ApiReply>>? _whenEmpty;

    /// <summary>Every JSON request, in order.</summary>
    public IReadOnlyList<ApiRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    /// <summary>Every path handed to the image fetch, in order.</summary>
    public IReadOnlyList<string> ByteFetches
    {
        get { lock (_requests) return _fetches.ToArray(); }
    }

    public int SendCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    public bool Disposed { get; private set; }

    /// <summary>What the image fetch returns, unless <see cref="ByteHandler"/> says otherwise.</summary>
    public byte[] Bytes { get; set; } = [0x89, 0x50, 0x4E, 0x47];

    public Func<string, CancellationToken, Task<byte[]>>? ByteHandler { get; set; }

    public void Reply(int statusCode, string body) =>
        _script.Enqueue((_, _) => Task.FromResult(new ApiReply(statusCode, body)));

    /// <summary>A call that never produced a response.</summary>
    public void Fail(string message = "the connection was reset") =>
        _script.Enqueue((_, _) => throw new ApiTransportException(message, new IOException(message)));

    /// <summary>A refusal envelope, in the shape the Go service writes it.</summary>
    public void Refuse(int statusCode, string code, string message, string requestId) =>
        Reply(statusCode, $$"""{"error":{"code":"{{code}}","message":"{{message}}","request_id":"{{requestId}}" } }""");

    /// <summary>A call that outlasts every deadline and is cancelled rather than answered.</summary>
    public void Hang() => _script.Enqueue((_, cancellationToken) => Never(cancellationToken));

    public void Answer(Func<ApiRequest, Task<ApiReply>> step) =>
        _script.Enqueue((request, _) => step(request));

    /// <summary>Answers every unscripted call the same way — the "service is down" mode.</summary>
    public void AlwaysFail(string message = "no route to host") =>
        _whenEmpty = (_, _) => throw new ApiTransportException(message, new IOException(message));

    /// <summary>Answers every unscripted call with a ticket nobody has scanned yet.</summary>
    public void AlwaysPending() =>
        _whenEmpty = (_, _) => Task.FromResult(new ApiReply(200, """{"status":"pending","expiresAt":"2026-03-01T12:05:00+00:00"}"""));

    public Task<ApiReply> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
            _requests.Add(request);

        cancellationToken.ThrowIfCancellationRequested();

        // An unscripted call is a bug in the test, not a scenario to paper over.
        var step = _script.Count > 0
            ? _script.Dequeue()
            : _whenEmpty ?? throw new InvalidOperationException(
                $"the fake transport had no answer scripted for {request.Method} {request.Path}");
        return step(request, cancellationToken);
    }

    public Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        lock (_requests)
            _fetches.Add(path);

        cancellationToken.ThrowIfCancellationRequested();

        return ByteHandler is not null ? ByteHandler(path, cancellationToken) : Task.FromResult(Bytes);
    }

    public void Dispose() => Disposed = true;

    private static async Task<ApiReply> Never(CancellationToken cancellationToken)
    {
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("a cancelled wait must not come back with an answer");
    }
}

/// <summary>
/// A clock the test moves by hand, and the delay the service waits through.
/// <para>
/// Advancing on the delay rather than on a wall clock is what turns the wait into an
/// assertion: the schedule's own numbers decide when the deadline passes, so the
/// test never depends on how fast the machine runs.
/// </para>
/// </summary>
internal class AccountClock
{
    public AccountClock(DateTimeOffset start) => Now = start;

    public DateTimeOffset Now { get; private set; }

    /// <summary>Every wait the service asked for, in order.</summary>
    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan amount) => Now = Now.Add(amount);

    public virtual Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Advance(delay);
        return Task.CompletedTask;
    }
}

/// <summary>A clock where the user closes the window after the first wait.</summary>
internal sealed class CancellableClock : AccountClock
{
    private readonly CancellationTokenSource _cancellation;
    private readonly int _after;

    public CancellableClock(DateTimeOffset start, CancellationTokenSource cancellation, int after)
        : base(start)
    {
        _cancellation = cancellation;
        _after = after;
    }

    public override async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await base.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        if (Delays.Count >= _after)
            await _cancellation.CancelAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The WeChat login: the QR handshake, the wait for a human, and the session that
/// outlives the app.
/// <para>
/// The class under test makes one promise above all the others, and it is stated in
/// its own doc comment: <i>it never talks when nobody is waiting</i>. Most of these
/// tests are about that sentence — the deadline, the backoff, the fact that one poll
/// is one request, and the fact that a service which never answers ends the wait
/// rather than the test.
/// </para>
/// </summary>
public sealed class AccountServiceTests
{
    /// <summary>A fixed instant, so nothing here depends on the machine's clock.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset TicketExpiresAt = new(2026, 3, 1, 12, 5, 0, TimeSpan.Zero);

    private const string Ticket = "0123456789abcdef0123456789abcdef";

    // ---- fixtures ----------------------------------------------------------

    private static AccountClock Clock() => new(Now);

    private static AccountService Service(
        FakeActivationTransport transport,
        AuthTokenStore? store = null,
        AccountClock? clock = null,
        PollSchedule? schedule = null,
        TimeSpan? timeout = null,
        List<string>? log = null)
    {
        Func<DateTimeOffset>? now = clock is null ? null : () => clock.Now;
        Func<TimeSpan, CancellationToken, Task>? wait = clock is null ? null : clock.WaitAsync;
        Action<string>? write = log is null ? null : log.Add;
        return new AccountService(transport, store, schedule, now, wait, timeout, write);
    }

    private static AuthTokenStore Store(TempInbox inbox) => new(inbox.Root, new FakeSecretProtector("machine-a"));

    /// <summary>A reload of what was written, through the same protector the file needs.</summary>
    private static AuthTokens? Reload(TempInbox inbox) =>
        new AuthTokenStore(inbox.Root, new FakeSecretProtector("machine-a")).Load();

    /// <summary>A session whose access token is good for another hour from <see cref="Now"/>.</summary>
    private static AuthTokens Tokens(string access = "access-1", string refresh = "refresh-1", string nickname = "小明") =>
        new(
            access,
            refresh,
            Now.AddHours(1),
            new AccountView("acct-1", nickname, "https://cdn.example/avatar.png", "perpetual", Now.AddDays(-30), Now.AddDays(-1), true));

    /// <summary>The wire shape of a session, as the Go service writes it.</summary>
    private static string TokensBody(string access = "access-2", string refresh = "refresh-2", string expires = "2026-03-01T13:00:00+00:00") =>
        $$"""
        {
          "accessToken": "{{access}}",
          "refreshToken": "{{refresh}}",
          "expiresAt": "{{expires}}",
          "account": { "id": "acct-1", "nickname": "小明", "plan": "perpetual", "wechatBound": true }
        }
        """;

    private static string TicketBody(string ticket = Ticket, string authorize = "https://wx.example/authorize", string qr = "v1/auth/session/0123456789abcdef0123456789abcdef/qr", string expires = "2026-03-01T12:05:00+00:00") =>
        $$"""
        { "ticket": "{{ticket}}", "authorizeUrl": "{{authorize}}", "qrImageUrl": "{{qr}}", "expiresAt": "{{expires}}" }
        """;

    private static string StatusBody(string status) =>
        $$"""{"status":"{{status}}","expiresAt":"2026-03-01T12:05:00+00:00"}""";

    private static string AuthorizedBody() =>
        """
        {
          "status": "authorized",
          "expiresAt": "2026-03-01T13:00:00+00:00",
          "accessToken": "access-2",
          "refreshToken": "refresh-2",
          "account": { "id": "acct-1", "nickname": "小明", "plan": "perpetual", "wechatBound": true }
        }
        """;

    private static JsonElement BodyOf(ApiRequest request) =>
        JsonDocument.Parse(request.Json ?? "{}").RootElement.Clone();

    // ---- construction and the stored session -------------------------------

    [Fact]
    public void TheStoredSessionIsReadOnceAndNeverAgain()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();

        var service = Service(transport, store, Clock());

        Assert.True(service.IsSignedIn);
        Assert.Equal("access-1", service.BearerToken);

        // The file going away underneath is not something the service notices: there
        // is exactly one place that knows whether we are signed in, and it is not
        // the disk.
        File.Delete(store.FilePath);
        Assert.True(service.IsSignedIn);
        Assert.Equal("access-1", service.BearerToken);
        Assert.Equal(0, transport.SendCount);

        // A service built later is the one that sees the truth.
        Assert.False(Service(new FakeActivationTransport(), store, Clock()).IsSignedIn);
    }

    [Fact]
    public void ATokenWithUnderAMinuteLeftIsAlreadyUnusable()
    {
        // The slack is in AuthTokens, not here, but the property that matters is
        // that IsSignedIn asks the clock rather than merely checking for null.
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var clock = Clock();

        var service = Service(new FakeActivationTransport(), store, clock);
        clock.Advance(TimeSpan.FromMinutes(59));

        Assert.False(service.IsSignedIn);
        // The session itself is still there, which is what makes a refresh possible.
        Assert.NotNull(service.Current);
    }

    [Fact]
    public void SigningInWithoutAStoreStillKeepsTheSessionInMemory()
    {
        var service = Service(new FakeActivationTransport(), clock: Clock());

        service.SignIn(Tokens());

        Assert.True(service.IsSignedIn);
        Assert.Equal("access-1", service.BearerToken);
    }

    [Fact]
    public void SigningInWritesTheSessionThroughToDisk()
    {
        using var inbox = new TempInbox();
        var log = new List<string>();
        var service = Service(new FakeActivationTransport(), Store(inbox), Clock(), log: log);

        service.SignIn(Tokens());

        // A session that only lived in memory would be lost at exit, and the whole
        // point of the refresh token is that "log in once" stays true.
        Assert.Equal(Tokens(), Reload(inbox));
        Assert.Contains(log, line => line.Contains("signed in as", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("小明", StringComparison.Ordinal));
    }

    [Fact]
    public void AUserWithNoNicknameIsCalledByTheirAccountId()
    {
        var log = new List<string>();
        var service = Service(new FakeActivationTransport(), log: log);

        service.SignIn(Tokens(nickname: "   "));

        Assert.Contains(log, line => line.Contains("acct-1", StringComparison.Ordinal));
    }

    [Fact]
    public void SigningInNothingIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => Service(new FakeActivationTransport()).SignIn(null!));

    [Fact]
    public void AServiceWithNoTransportIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new AccountService(null!));

    [Fact]
    public void DisposingTheServiceDisposesTheTransport()
    {
        var transport = new FakeActivationTransport();
        Service(transport).Dispose();

        Assert.True(transport.Disposed);
    }

    // ---- asking for a ticket ----------------------------------------------

    [Fact]
    public async Task TheTicketRequestCarriesTheMachineIdentityAndNothingElse()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(201, TicketBody());
        var service = Service(transport);

        var ticket = await service.BeginLoginAsync("install-abc", "device-xyz", CancellationToken.None);

        var request = Assert.Single(transport.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(ActivationEndpoint.SessionPath, request.Path);
        // Anonymous, like every other call in this flow: the install id is a random
        // number this product generated, and nothing here is a credential yet.
        Assert.Null(request.BearerToken);

        var body = BodyOf(request);
        Assert.Equal("install-abc", body.GetProperty("installId").GetString());
        Assert.Equal("device-xyz", body.GetProperty("deviceId").GetString());
        Assert.Equal(2, body.EnumerateObject().Count());

        Assert.Equal(Ticket, ticket.Ticket);
        Assert.Equal("https://wx.example/authorize", ticket.AuthorizeUrl);
        Assert.Equal($"v1/auth/session/{Ticket}/qr", ticket.QrImagePath);
        Assert.Equal(TicketExpiresAt, ticket.ExpiresAt);
    }

    [Fact]
    public async Task ATicketWithoutOptionalFieldsStillReads()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(200, $$"""{"ticket":"{{Ticket}}"}""");
        var clock = Clock();

        var ticket = await Service(transport, clock: clock).BeginLoginAsync("install-abc", null, CancellationToken.None);

        Assert.Equal(string.Empty, ticket.AuthorizeUrl);
        Assert.Equal(string.Empty, ticket.QrImagePath);
        // No expiry in the answer means "as far as we know, now" — never a default
        // of year one, which would read as long expired.
        Assert.Equal(clock.Now, ticket.ExpiresAt);
    }

    [Fact]
    public async Task ARefusalCarriesTheServicesOwnMessageAndCorrelationId()
    {
        var transport = new FakeActivationTransport();
        transport.Refuse(400, ApiErrorCodes.BadRequest, "安装标识无效。", "req-abc-123");

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(transport).BeginLoginAsync("install-abc", null, CancellationToken.None));

        // The service's Chinese is written for a support conversation; inventing a
        // second one here is how the two drift apart.
        Assert.Equal("安装标识无效。", error.Message);
        Assert.Equal(ApiErrorCodes.BadRequest, error.Code);
        Assert.Equal("req-abc-123", error.RequestId);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task ARefusalTheServiceDidNotExplainGetsALocalMessage()
    {
        var transport = new FakeActivationTransport();
        // A proxy's HTML error page, which is what actually arrives in this case.
        transport.Reply(502, "<html><body>Bad Gateway</body></html>");

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(transport).BeginLoginAsync("install-abc", null, CancellationToken.None));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(ApiErrorCodes.Internal, error.Code);
        Assert.Equal(string.Empty, error.RequestId);
        Assert.Contains("502", error.Message, StringComparison.Ordinal);
        Assert.True(error.IsRetryable);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsRefusedRatherThanGuessedAt()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(200, "not json at all");

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(transport).BeginLoginAsync("install-abc", null, CancellationToken.None));

        Assert.Contains("无法识别", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task NoTicketIsCreatedWithoutAnInstallId()
    {
        var transport = new FakeActivationTransport();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Service(transport).BeginLoginAsync(null, null, CancellationToken.None));

        // The parameter is nullable so the compiler will not help; this is the check.
        Assert.Equal(0, transport.SendCount);
    }

    // ---- the QR image ------------------------------------------------------

    [Fact]
    public async Task TheQrImageIsFetchedThroughTheTransport()
    {
        var transport = new FakeActivationTransport { Bytes = new byte[] { 1, 2, 3, 4 } };

        var bytes = await Service(transport).FetchQrAsync(
            new SessionTicket(Ticket, "", "/v1/auth/session/x/qr", Now),
            CancellationToken.None);

        // A relative path, and the leading slash stripped: "/v1/..." resolved against
        // the base address would replace its last segment and fetch a different URL.
        Assert.Equal("v1/auth/session/x/qr", Assert.Single(transport.ByteFetches));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task ATicketWithNoQrPathFallsBackToTheDerivedOne()
    {
        var transport = new FakeActivationTransport();

        await Service(transport).FetchQrAsync(new SessionTicket(Ticket, "", string.Empty, Now), CancellationToken.None);

        Assert.Equal($"v1/auth/session/{Ticket}/qr", Assert.Single(transport.ByteFetches));
    }

    [Fact]
    public async Task AQrFetchThatOverrunsItsDeadlineIsATimeoutRatherThanACancel()
    {
        // HttpClient raises the same exception for both, and after the fact nothing
        // can tell them apart — which is why the deadline is a linked token.
        var transport = new FakeActivationTransport
        {
            ByteHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            },
        };

        await Assert.ThrowsAsync<ApiTimeoutException>(
            () => Service(transport, timeout: TimeSpan.FromMilliseconds(100))
                .FetchQrAsync(new SessionTicket(Ticket, "", string.Empty, Now), CancellationToken.None));
    }

    [Fact]
    public async Task AQrFetchTheUserCancelledIsStillACancel()
    {
        var transport = new FakeActivationTransport
        {
            ByteHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            },
        };
        using var cancellation = new CancellationTokenSource();

        var fetch = Service(transport).FetchQrAsync(new SessionTicket(Ticket, "", string.Empty, Now), cancellation.Token);
        await cancellation.CancelAsync();

        // Dressing this up as a timeout would send the UI looking for a network
        // problem the user caused by closing the window.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.IsNotType<ApiTimeoutException>(error);
    }

    // ---- one poll ----------------------------------------------------------

    [Fact]
    public async Task OnePollIsExactlyOneRequest()
    {
        // The sentence the whole class is built around: a poll is something the
        // caller asked for once. If this ever becomes two, the app is talking while
        // nobody is waiting.
        var transport = new FakeActivationTransport();
        transport.Reply(200, StatusBody(AuthStatus.Pending));
        var clock = Clock();

        var poll = await Service(transport, clock: clock).PollAsync(Ticket, CancellationToken.None);

        Assert.True(poll.IsPending);
        Assert.Null(poll.Tokens);
        Assert.Equal(1, transport.SendCount);
        Assert.Empty(clock.Delays);
        Assert.Equal("GET", transport.Requests[0].Method);
        Assert.Equal(ActivationEndpoint.SessionTicketPath(Ticket), transport.Requests[0].Path);
        Assert.Null(transport.Requests[0].BearerToken);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task ATicketTheServiceHasForgottenIsAnExpiryRatherThanAnError(int statusCode)
    {
        var transport = new FakeActivationTransport();
        transport.Reply(statusCode, string.Empty);

        var poll = await Service(transport).PollAsync(Ticket, CancellationToken.None);

        // "Scan again" is the answer; an exception would be a stack trace instead.
        Assert.True(poll.IsExpired);
        Assert.Null(poll.Tokens);
    }

    [Fact]
    public async Task APollRefusalCarriesTheServicesMessage()
    {
        var transport = new FakeActivationTransport();
        transport.Refuse(500, ApiErrorCodes.Internal, "服务暂时不可用。", "req-9");

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(transport).PollAsync(Ticket, CancellationToken.None));

        Assert.Equal("服务暂时不可用。", error.Message);
        Assert.Equal("req-9", error.RequestId);
    }

    [Fact]
    public async Task AnUnreadablePollIsRefusedRatherThanGuessedAt()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(200, "<html>");

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(transport).PollAsync(Ticket, CancellationToken.None));

        Assert.Contains("无法识别", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyTicketNeverReachesTheService()
    {
        var transport = new FakeActivationTransport();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service(transport).PollAsync(string.Empty, CancellationToken.None));

        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task ATicketWithAwkwardCharactersCannotSteerThePath()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(200, StatusBody(AuthStatus.Pending));

        await Service(transport).PollAsync("../../admin", CancellationToken.None);

        // A ticket is server-issued, but escaping it anyway means a careless service
        // cannot turn a poll into a request somewhere else.
        Assert.Equal(ActivationEndpoint.SessionTicketPath("../../admin"), transport.Requests[0].Path);
        Assert.DoesNotContain("../", transport.Requests[0].Path, StringComparison.Ordinal);
    }

    // ---- the wait for a human ---------------------------------------------

    [Fact]
    public async Task AScanSignsInAndStopsAsking()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        var transport = new FakeActivationTransport();
        transport.Reply(200, AuthorizedBody());
        var clock = Clock();
        var service = Service(transport, store, clock);

        var outcome = await service.WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromMinutes(5));

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(LoginFailure.None, outcome.Failure);
        Assert.Equal("access-2", outcome.Tokens!.AccessToken);
        Assert.Equal("access-2", service.BearerToken);
        Assert.Equal("refresh-2", service.Current!.RefreshToken);
        Assert.True(service.IsSignedIn);
        Assert.NotNull(Reload(inbox));

        // One request, and not one wait before it: the user is looking at the code.
        Assert.Equal(1, transport.SendCount);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task TheWaitStopsAtTheCallersDeadline()
    {
        var transport = new FakeActivationTransport();
        transport.AlwaysPending();
        var clock = Clock();
        var budget = TimeSpan.FromSeconds(30);

        var outcome = await Service(transport, clock: clock)
            .WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal(LoginFailure.TimedOut, outcome.Failure);
        Assert.Contains("超时", outcome.Detail, StringComparison.Ordinal);

        // Bounded by the deadline rather than by the schedule's taste: at most one
        // more request past the budget, because a wait already under way is never
        // cut short mid-interval.
        Assert.True(clock.Now - Now <= budget + PollSchedule.Login.Ceiling,
            $"the wait ran to {clock.Now - Now}, past the {budget} budget by more than one interval");
        Assert.InRange(transport.SendCount, 2, PollSchedule.Login.AttemptsWithin(budget) + 2);
    }

    [Fact]
    public async Task TheWaitUsesTheScheduleAndThenSettles()
    {
        var transport = new FakeActivationTransport();
        transport.AlwaysPending();
        var clock = Clock();

        await Service(transport, clock: clock)
            .WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromSeconds(60));

        // One fewer wait than requests: the last poll is the one that hits the deadline.
        Assert.Equal(transport.SendCount - 1, clock.Delays.Count);
        for (var attempt = 0; attempt < clock.Delays.Count; attempt++)
            Assert.Equal(PollSchedule.Login.DelayBefore(attempt), clock.Delays[attempt]);

        // A user who walks away leaves the app going quiet rather than going loud.
        Assert.Contains(PollSchedule.Login.Ceiling, clock.Delays);
        for (var i = 1; i < clock.Delays.Count; i++)
            Assert.True(clock.Delays[i] >= clock.Delays[i - 1], $"wait {i} went backwards");
    }

    [Fact]
    public async Task AScheduleThatIsInjectedReplacesTheLoginCadence()
    {
        var transport = new FakeActivationTransport();
        transport.AlwaysPending();
        var clock = Clock();
        var quiet = new PollSchedule(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), 2.0);

        await Service(transport, clock: clock, schedule: quiet)
            .WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(30), clock.Delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.Delays[1]);
    }

    [Fact]
    public async Task AnExpiredTicketEndsTheWaitWithoutAFurtherRequest()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(200, StatusBody(AuthStatus.Pending));
        transport.Reply(404, string.Empty);
        var clock = Clock();

        var outcome = await Service(transport, clock: clock)
            .WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromMinutes(5));

        Assert.Equal(LoginFailure.TicketExpired, outcome.Failure);
        // A dead ticket is a dead ticket: no third poll, and no wait after the answer.
        Assert.Equal(2, transport.SendCount);
        Assert.Single(clock.Delays);
    }

    [Fact]
    public async Task AFailedPollIsNotAFailedLogin()
    {
        using var inbox = new TempInbox();
        var transport = new FakeActivationTransport();
        transport.Fail("the connection was reset");
        transport.Fail("timed out");
        transport.Reply(200, StatusBody(AuthStatus.Pending));
        transport.Reply(200, AuthorizedBody());
        var clock = Clock();
        var seen = new List<string>();

        var outcome = await Service(transport, Store(inbox), clock: clock).WaitForScanAsync(
            new SessionTicket(Ticket, "", "", Now),
            TimeSpan.FromMinutes(5),
            poll => seen.Add(poll.Status));

        // The ticket was never burned, so the next attempt is free and the login
        // still succeeds on the same ticket.
        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(4, transport.SendCount);
        // The two answers that arrived are what the window renders; the two that did
        // not are not states it can show.
        Assert.Equal(new[] { AuthStatus.Pending, AuthStatus.Authorized }, seen.ToArray());
        Assert.Equal(3, clock.Delays.Count);
    }

    [Fact]
    public async Task AServiceThatNeverAnswersEndsTheWaitAsUnreachable()
    {
        var transport = new FakeActivationTransport();
        transport.AlwaysFail();
        var clock = Clock();
        var seen = new List<string>();
        var log = new List<string>();

        var outcome = await Service(transport, clock: clock, log: log).WaitForScanAsync(
            new SessionTicket(Ticket, "", "", Now),
            TimeSpan.FromSeconds(30),
            poll => seen.Add(poll.Status));

        Assert.Equal(LoginFailure.Unreachable, outcome.Failure);
        Assert.Contains("没能联系上", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(seen);
        // "asking again is free" — the ticket is still good, and the log says so
        // without alarming anyone.
        Assert.Contains(log, line => line.Contains("did not reach the service", StringComparison.Ordinal));
        Assert.True(clock.Now - Now <= TimeSpan.FromSeconds(30) + PollSchedule.Login.Ceiling,
            $"the wait ran to {clock.Now - Now}");
        Assert.InRange(transport.SendCount, 2, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(30)) + 2);
    }

    [Fact]
    public async Task ACancelledWaitStopsAsking()
    {
        var transport = new FakeActivationTransport();
        transport.AlwaysPending();
        using var cancellation = new CancellationTokenSource();
        var clock = new CancellableClock(Now, cancellation, after: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(transport, clock: clock).WaitForScanAsync(
                new SessionTicket(Ticket, "", "", Now),
                TimeSpan.FromMinutes(5),
                cancellationToken: cancellation.Token));

        // Closing the window is the user's decision, not a login failure to report.
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task AWaitThatIsCancelledBeforeItStartsSendsNothing()
    {
        var transport = new FakeActivationTransport();
        transport.AlwaysPending();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(transport, clock: Clock()).WaitForScanAsync(
                new SessionTicket(Ticket, "", "", Now),
                TimeSpan.FromMinutes(5),
                cancellationToken: cancellation.Token));

        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task ACallThatOutrunsItsDeadlineIsATimeoutRatherThanACancel()
    {
        var transport = new FakeActivationTransport();
        transport.Hang();

        await Assert.ThrowsAsync<ApiTimeoutException>(
            () => Service(transport, timeout: TimeSpan.FromMilliseconds(100))
                .BeginLoginAsync("install-abc", null, CancellationToken.None));
    }

    [Fact]
    public async Task OneHungPollEndsTheWaitInsteadOfHangingIt()
    {
        // A black-holed connection is a timeout, and a timeout is not the same thing
        // as "the service is not answering" — so it does not go through the backoff.
        // The window surfaces it rather than the app retrying quietly underneath.
        var transport = new FakeActivationTransport();
        transport.Hang();
        var clock = Clock();

        await Assert.ThrowsAsync<ApiTimeoutException>(
            () => Service(transport, clock: clock, timeout: TimeSpan.FromMilliseconds(100))
                .WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromMinutes(5)));

        Assert.Equal(1, transport.SendCount);
        Assert.Empty(clock.Delays);
    }

    // ---- the refresh token -------------------------------------------------

    [Fact]
    public async Task ARefreshWithNoSessionNeverReachesTheService()
    {
        var transport = new FakeActivationTransport();

        var outcome = await Service(transport).RefreshAsync(CancellationToken.None);

        Assert.Equal(LoginFailure.SessionExpired, outcome.Failure);
        Assert.Equal("尚未登录。", outcome.Detail);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task ARefreshReplacesTheStoredSession()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Reply(200, TokensBody());
        var service = Service(transport, store);

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal("access-2", service.BearerToken);
        Assert.Equal("refresh-2", Reload(inbox)!.RefreshToken);

        var request = Assert.Single(transport.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(ActivationEndpoint.RefreshPath, request.Path);
        // The refresh token travels in the body, not as a bearer header — so a log
        // line that shows the path shows nothing usable to a bystander.
        Assert.Null(request.BearerToken);
        Assert.Equal("refresh-1", BodyOf(request).GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task AServiceThatCannotBeReachedKeepsTheSessionForNextTime()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Fail("no route to host");
        var service = Service(transport, store, Clock());

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(LoginFailure.Unreachable, outcome.Failure);
        // Throwing the session away over a network blip would cost a QR scan for
        // something that was never the session's fault.
        Assert.True(service.IsSignedIn);
        Assert.NotNull(Reload(inbox));
    }

    [Theory]
    [InlineData(400, ApiErrorCodes.BadRequest)]
    [InlineData(401, ApiErrorCodes.Unauthorized)]
    public async Task ARefreshTokenTheServiceRefusesEndsTheSessionHere(int statusCode, string code)
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Refuse(statusCode, code, "登录已过期。", "req-1");
        var service = Service(transport, store, Clock());

        var outcome = await service.RefreshAsync(CancellationToken.None);

        // A token the server will never take again must not be kept around to fail
        // again on every launch.
        Assert.Equal(LoginFailure.SessionExpired, outcome.Failure);
        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task AnUnreadableRefreshIsMalformedAndStillEndsTheSession()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        // Well-formed JSON that is not a session. Half a session is not a session;
        // keeping it would leave the next launch signing every request with a token
        // nobody can refresh.
        transport.Reply(200, """{"accessToken":null,"refreshToken":null}""");
        var service = Service(transport, store, Clock());

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(LoginFailure.MalformedResponse, outcome.Failure);
        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task ARefreshAnswerThatIsNotJsonAtAllIsMalformedAndStillEndsTheSession()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        // A proxy's login page, in front of a service that is merely unreachable.
        transport.Reply(200, "<html>please sign in again</html>");
        var service = Service(transport, store, Clock());

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(LoginFailure.MalformedResponse, outcome.Failure);
        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task ARefreshRoundTripIsHowSomebodyGoesMonthsWithoutScanning()
    {
        // The doc comment calls this the common case rather than the exceptional
        // one: the access token has aged out, the refresh token is still good, and
        // nobody sees a QR code.
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Reply(200, TokensBody(expires: "2026-04-30T13:00:00+00:00"));
        var clock = Clock();
        var service = Service(transport, store, clock);

        clock.Advance(TimeSpan.FromDays(60));
        Assert.False(service.IsSignedIn);      // the stored access token has aged out

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.True(service.IsSignedIn);
    }

    // ---- signing out -------------------------------------------------------

    [Fact]
    public async Task SigningOutClearsTheLocalSessionFirstAndThenAsksToRevoke()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Reply(204, string.Empty);
        var service = Service(transport, store, Clock());

        await service.SignOutAsync(CancellationToken.None);

        // A customer who clicked 退出 on a shared machine means it: the file is gone
        // before anything is sent, so an interrupted revoke cannot leave a session
        // behind for the next launch to pick up.
        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));

        var request = Assert.Single(transport.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal(ActivationEndpoint.SessionPath, request.Path);
        Assert.Null(request.BearerToken);
        Assert.Equal("refresh-1", BodyOf(request).GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task ARevokeThatNeverReachesTheServiceStillSignsTheUserOut()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Fail("the connection was reset");
        var log = new List<string>();
        var service = Service(transport, store, Clock(), log: log);

        await service.SignOutAsync(CancellationToken.None);

        // The local half is what the user asked for; a revoke is a courtesy to the
        // server and to its own TTL.
        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
        Assert.Contains(log, line => line.Contains("local session is gone anyway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServiceThatRefusesToRevokeStillSignsTheUserOut()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        // The common shape: the server has already forgotten the token.
        transport.Reply(401, string.Empty);
        var service = Service(transport, store, Clock());

        await service.SignOutAsync(CancellationToken.None);

        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task ARevokeTheUserCancelledStillSignsTheUserOut()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        transport.Hang();
        var service = Service(transport, store, Clock());
        using var cancellation = new CancellationTokenSource();

        var revoke = service.SignOutAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await revoke;

        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task SigningOutWithNothingToRevokeNeverReachesTheService()
    {
        var transport = new FakeActivationTransport();

        await Service(transport).SignOutAsync(CancellationToken.None);

        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public void SigningOutLocallyNeverReachesTheService()
    {
        using var inbox = new TempInbox();
        var store = Store(inbox);
        store.Save(Tokens());
        var transport = new FakeActivationTransport();
        var service = Service(transport, store, Clock());

        service.SignOutLocally();

        Assert.False(service.IsSignedIn);
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal(0, transport.SendCount);
    }

    // ---- the promises the whole flow rests on ------------------------------

    [Fact]
    public async Task NothingInTheWholeFlowEverSendsABearerToken()
    {
        // The buy-out wording says this build talks to the licence service and
        // nothing else, and the login/refresh pair proves it: every call is either
        // anonymous or carries its credential in the body, so a proxy log never holds
        // something that authorises a purchase.
        var transport = new FakeActivationTransport();
        transport.Reply(201, TicketBody());
        transport.Reply(200, AuthorizedBody());
        transport.Reply(200, TokensBody());
        transport.Reply(204, string.Empty);
        var service = Service(transport, clock: Clock());

        await service.BeginLoginAsync("install-abc", "device-xyz", CancellationToken.None);
        await service.WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromMinutes(5));
        await service.RefreshAsync(CancellationToken.None);
        await service.SignOutAsync(CancellationToken.None);

        Assert.Equal(4, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Null(request.BearerToken));
    }

    [Fact]
    public async Task EveryCallStaysInsideTheNamedEndpoints()
    {
        var transport = new FakeActivationTransport();
        transport.Reply(201, TicketBody());
        transport.Reply(200, AuthorizedBody());
        transport.Reply(200, TokensBody());
        transport.Reply(204, string.Empty);
        var service = Service(transport, clock: Clock());

        await service.BeginLoginAsync("install-abc", null, CancellationToken.None);
        await service.WaitForScanAsync(new SessionTicket(Ticket, "", "", Now), TimeSpan.FromMinutes(5));
        await service.RefreshAsync(CancellationToken.None);
        await service.SignOutAsync(CancellationToken.None);

        Assert.All(transport.Requests, request =>
        {
            Assert.StartsWith("v1/", request.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("://", request.Path, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(409, false)]
    public async Task OnlyTheOutcomesThatCouldChangeOnASecondTryAreRetryable(int statusCode, bool expected)
    {
        // 404 and 410 are not in this table: a poll that gets one is a dead ticket,
        // not a refusal, so it never becomes an ApiException to classify.
    {
        var transport = new FakeActivationTransport();
        transport.Reply(statusCode, string.Empty);

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(transport).PollAsync(Ticket, CancellationToken.None));

        // A 401 is the token's problem. Offering "retry" there is how a user ends up
        // convinced they are being rate-limited.
        Assert.Equal(expected, error.IsRetryable);
    }
}
}
