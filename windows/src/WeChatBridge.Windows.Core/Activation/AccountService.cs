using System.IO;
using System.Text.Json;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>Why a login call did not produce a session.</summary>
public enum LoginFailure
{
    None,

    /// <summary>The service could not be reached. The ticket is still good; asking again is free.</summary>
    Unreachable,

    /// <summary>The QR ticket expired before the user scanned it.</summary>
    TicketExpired,

    /// <summary>The user closed the window, or the caller cancelled.</summary>
    Cancelled,

    /// <summary>The deadline passed while waiting for the user to scan.</summary>
    TimedOut,

    /// <summary>The service answered with something this client cannot read.</summary>
    MalformedResponse,

    /// <summary>The stored refresh token is no longer accepted.</summary>
    SessionExpired,
}

/// <summary>The result of one login attempt.</summary>
public sealed record LoginOutcome(LoginFailure Failure, string Detail, AuthTokens? Tokens = null)
{
    public bool Succeeded => Failure == LoginFailure.None && Tokens is not null;

    public static LoginOutcome Success(AuthTokens tokens) =>
        new(LoginFailure.None, L10n.Text("签到成功。"), tokens);

    public static LoginOutcome Failed(LoginFailure failure, string detail) => new(failure, detail);
}

/// <summary>
/// The WeChat login: create a QR ticket, wait for the scan, keep the tokens.
/// <para>
/// <b>What this class is responsible for.</b> Turning "the user is looking at a QR
/// code" into a bearer token, and remembering that token afterwards. It deliberately
/// knows nothing about windows, buttons or state machines — the polling loop lives
/// here because it is the part worth testing, and the UI is a rendering of it.
/// </para>
/// <para>
/// <b>What it will not do.</b> It never retries on its own. Every wait here is either
/// driven by the caller or bounded by a deadline the caller supplied, because "the app
/// sent a request while I was doing nothing" is the one failure this product's network
/// promise cannot survive.
/// </para>
/// </summary>
public sealed class AccountService : IDisposable
{
    private readonly IActivationTransport _transport;
    private readonly AuthTokenStore? _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;
    private readonly PollSchedule _schedule;

    private AuthTokens? _current;

    public AccountService(
        IActivationTransport transport,
        AuthTokenStore? store = null,
        PollSchedule? schedule = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _store = store;
        _schedule = schedule ?? PollSchedule.Login;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? ActivationEndpoint.DefaultTimeout;
        _log = log;

        // Read the stored session once, at construction. Every later read goes
        // through Current so there is exactly one place that knows whether we are
        // signed in.
        _current = _store?.Load();
    }

    /// <summary>The live session, or null. Never reads from disk again.</summary>
    public AuthTokens? Current => _current;

    /// <summary>Raised after the session or profile is persisted; may run on a worker thread.</summary>
    public event Action? Changed;

    /// <summary>The access token to authorise a call with, or null when signed out.</summary>
    public string? BearerToken => _current?.AccessToken;

    public bool IsSignedIn => _current is not null && _current.IsUsableAt(_clock());

    /// <summary>Adopts a session obtained elsewhere and writes it through to disk.</summary>
    public void SignIn(AuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _current = tokens;
        _store?.Save(tokens);
        Changed?.Invoke();
        _log?.Invoke($"account: signed in as {tokens.Account.DisplayName}");
    }

    /// <summary>
    /// Forgets the session. Asks the server to revoke it too, best effort: a
    /// customer who clicked 退出 on a shared machine means it, and a revoke that
    /// fails must not stop the local half.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var refresh = _current?.RefreshToken;
        SignOutLocally();

        if (refresh is null)
            return;
        try
        {
            var json = JsonSerializer.Serialize(new { refreshToken = refresh }, ActivationJson.Options);
            await SendAsync(ActivationEndpoint.SessionPath, "DELETE", json, bearer: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiTransportException error)
        {
            // The local session is already gone, which is what the user asked for.
            _log?.Invoke($"account: sign-out revoke did not reach the service ({error.Message}); the local session is gone anyway.");
        }
        catch (ApiException)
        {
            // Same reasoning: the server may refuse to revoke a token it has
            // already forgotten, and that is not worth blocking a sign-out over.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Asks the service for a QR ticket. Nothing is sent but the install id, which
    /// is a random number this product generated — it ties the ticket to a machine
    /// so a stray scan cannot be redeemed elsewhere.
    /// </summary>
    public async Task<SessionTicket> BeginLoginAsync(string? installId, string? deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installId);
        var json = JsonSerializer.Serialize(new { installId, deviceId }, ActivationJson.Options);
        var reply = await SendAsync(ActivationEndpoint.SessionPath, json, bearer: null, cancellationToken).ConfigureAwait(false);

        if (!reply.IsSuccess)
        {
            var failure = ApiFailure.TryParse(reply.Body);
            throw new ApiException(reply.StatusCode,
                failure?.Message ?? L10n.Format($"创建登录会话失败（HTTP {reply.StatusCode}）。"),
                failure?.Code ?? ApiErrorCodes.Internal,
                failure?.RequestId ?? "");
        }

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            // The ticket is the one field with no sensible default: without it
            // there is nothing to poll, and the expiry is what bounds the wait.
            // The Go DTO declares expiresAt as a pointer, so a null there is a
            // payload this client cannot use rather than a licence to guess.
            var ticket = JsonPayload.String(root, "ticket");
            if (string.IsNullOrEmpty(ticket))
                throw new JsonException("The session answer carries no ticket.");
            var expiresAt = JsonPayload.Timestamp(root, "expiresAt")
                ?? throw new JsonException("The session answer carries no expiry.");
            return new SessionTicket(
                ticket,
                JsonPayload.String(root, "authorizeUrl") ?? "",
                JsonPayload.String(root, "qrImageUrl") ?? "",
                expiresAt);
        }
        catch (JsonException error)
        {
            throw new ApiException(reply.StatusCode, L10n.Text("登录服务返回了无法识别的内容。"), ApiErrorCodes.Internal, "", error);
        }
    }

    /// <summary>
    /// Fetches the QR image the user is about to scan.
    /// <para>
    /// Through the transport rather than by handing a URL to the image control: a
    /// window that loads an <c>http</c> URI itself gets a fetch with no deadline and
    /// no audit trail, and the privacy wording says every byte goes through one
    /// place. The path is whatever the server put in <c>qrImageUrl</c>, and the
    /// leading slash is stripped so a relative path resolves against the base address
    /// rather than replacing its last segment.
    /// </para>
    /// </summary>
    public async Task<byte[]> FetchQrAsync(SessionTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var path = ticket.QrImagePath.TrimStart('/');
        if (path.Length == 0)
            path = ActivationEndpoint.QrImagePath(ticket.Ticket).TrimStart('/');

        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _transport.GetBytesAsync(path, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The seconds are rendered here rather than through a format
            // specifier in the literal: the specifier would become part of
            // the translation key ("{0:0}"), and a key nobody can write by
            // hand is a key that will not be found.
            throw new ApiTimeoutException(L10n.Format($"登录服务在 {_timeout.TotalSeconds.ToString("0", L10n.Culture)} 秒内没有返回二维码。"), error);
        }
    }

    /// <summary>One poll of a ticket. Never polls by itself.</summary>
    public async Task<SessionPoll> PollAsync(string ticket, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticket);
        var reply = await SendAsync(ActivationEndpoint.SessionTicketPath(ticket), null, bearer: null, cancellationToken)
            .ConfigureAwait(false);

        if (reply.StatusCode == 404 || reply.StatusCode == 410)
            return new SessionPoll(AuthStatus.Expired, null, null);

        if (!reply.IsSuccess)
        {
            var failure = ApiFailure.TryParse(reply.Body);
            throw new ApiException(reply.StatusCode,
                failure?.Message ?? L10n.Format($"查询登录状态失败（HTTP {reply.StatusCode}）。"),
                failure?.Code ?? ApiErrorCodes.Internal,
                failure?.RequestId ?? "");
        }

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            // The status is what the poll is for: absent, and there is nothing
            // to wait on.
            var status = JsonPayload.String(root, "status");
            if (string.IsNullOrEmpty(status))
                throw new JsonException("The poll answer carries no status.");
            DateTimeOffset? expires = JsonPayload.Timestamp(root, "expiresAt");

            if (!string.Equals(status, AuthStatus.Authorized, StringComparison.Ordinal))
                return new SessionPoll(status, expires, null);

            var tokens = ReadTokens(root);
            return new SessionPoll(AuthStatus.Authorized, expires, tokens);
        }
        catch (JsonException error)
        {
            throw new ApiException(reply.StatusCode, L10n.Text("登录服务返回了无法识别的内容。"), ApiErrorCodes.Internal, "", error);
        }
    }

    /// <summary>
    /// Polls until the user scans, the ticket expires, or the deadline passes.
    /// <para>
    /// The delay before each attempt comes from <see cref="PollSchedule"/>, so a
    /// user who walks away leaves the app going quiet rather than going loud. The
    /// injected <c>delay</c> makes that cadence a unit test instead of a sleep.
    /// </para>
    /// </summary>
    public async Task<LoginOutcome> WaitForScanAsync(
        SessionTicket ticket,
        TimeSpan budget,
        Action<SessionPoll>? onPoll = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var deadlineAt = _clock().Add(budget);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SessionPoll poll;
            try
            {
                poll = await PollAsync(ticket.Ticket, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiTransportException error)
            {
                // A failed poll is not a failed login: the ticket is untouched and the
                // next attempt is free. Give up only when the ticket itself is dead.
                _log?.Invoke($"account: poll {attempt} did not reach the service ({error.Message})");
                if (_clock() >= deadlineAt)
                    return LoginOutcome.Failed(LoginFailure.Unreachable, L10n.Text("没能联系上登录服务，请检查网络后重试。"));
                await BackoffAsync(attempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (onPoll is not null)
                onPoll(poll);

            if (poll.IsAuthorized)
            {
                SignIn(poll.Tokens!);
                return LoginOutcome.Success(poll.Tokens!);
            }

            if (poll.IsExpired)
                return LoginOutcome.Failed(LoginFailure.TicketExpired, L10n.Text("二维码已过期，请刷新后重新扫码。"));

            if (_clock() >= deadlineAt)
                return LoginOutcome.Failed(LoginFailure.TimedOut, L10n.Text("等待扫码超时，请刷新后重新扫码。"));

            await BackoffAsync(attempt++, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Trades a refresh token for a new session. Called when a stored access token
    /// has aged out, which is the common case rather than the exceptional one: it is
    /// how a customer goes months without scanning.
    /// </summary>
    public async Task<LoginOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        var refresh = _current?.RefreshToken;
        if (refresh is null)
            return LoginOutcome.Failed(LoginFailure.SessionExpired, L10n.Text("尚未登录。"));

        var json = JsonSerializer.Serialize(new { refreshToken = refresh }, ActivationJson.Options);
        ApiReply reply;
        try
        {
            reply = await SendAsync(ActivationEndpoint.RefreshPath, json, bearer: null, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiTransportException error)
        {
            return LoginOutcome.Failed(LoginFailure.Unreachable, L10n.Format($"没能联系上登录服务：{error.Message}"));
        }

        if (!reply.IsSuccess)
        {
            // The server keeps its own view of whether a refresh token is good; if it
            // says no, the honest thing is to make the user scan again rather than
            // retry with a token that will never work.
            SignOutLocally();
            return LoginOutcome.Failed(LoginFailure.SessionExpired, L10n.Text("登录已过期，请重新扫码登录。"));
        }

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var tokens = ReadTokens(document.RootElement);
            SignIn(tokens);
            return LoginOutcome.Success(tokens);
        }
        catch (JsonException)
        {
            SignOutLocally();
            return LoginOutcome.Failed(LoginFailure.MalformedResponse, L10n.Text("登录服务返回了无法识别的内容，请重新扫码登录。"));
        }
    }

    /// <summary>Drops the session without talking to the service.</summary>
    public void SignOutLocally()
    {
        _current = null;
        _store?.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Asks the service who the bearer belongs to right now. The endpoint exists to
    /// let the client answer "am I still signed in?" without re-running the QR flow:
    /// a 200 means the access token still verifies and the cached profile is fresh,
    /// a 401 means the session has aged out server-side and the next thing the user
    /// needs to do is scan again.
    /// <para>
    /// <b>What gets refreshed, and what does not.</b> The reply is the server's
    /// authoritative view of this account — nickname, plan, last-login time —
    /// because those drift without the client noticing and a UI showing a stale
    /// nickname looks like the licence had a brain fart. The tokens themselves are
    /// not rotated here: a token that would need rotating can no longer reach this
    /// endpoint, and the chain's whole life is the refresh path's job.
    /// </para>
    /// <para>
    /// <b>On a 401 we drop the local session.</b> The server is the one telling us
    /// the credential is dead, and a client that keeps the dead token around will
    /// try again on the next launch, fail again, and present a "you're signed in"
    /// pane that has not been true for a week. Clearing <see cref="_current"/> here
    /// is what keeps that lie from compiling.
    /// </para>
    /// </summary>
    public async Task<LoginOutcome> MeAsync(CancellationToken cancellationToken)
    {
        var access = BearerToken;
        if (string.IsNullOrWhiteSpace(access))
            return LoginOutcome.Failed(LoginFailure.SessionExpired, L10n.Text("尚未登录。"));

        var reply = await SendAsync(ActivationEndpoint.MePath, null, bearer: access, cancellationToken).ConfigureAwait(false);

        if (reply.StatusCode == 401)
        {
            SignOutLocally();
            return LoginOutcome.Failed(LoginFailure.SessionExpired, L10n.Text("登录已过期，请重新扫码。"));
        }
        if (!reply.IsSuccess)
            return LoginOutcome.Failed(LoginFailure.Unreachable, L10n.Format($"查询会话状态失败（HTTP {reply.StatusCode}）。"));

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var account = ReadAccount(document.RootElement);
            if (_current is null)
            {
                // The server says yes, the disk says no. Treat it as logged out so
                // the caller does the right thing on the next launch — and not
                // invent a token out of thin air to "save" the response.
                return LoginOutcome.Failed(LoginFailure.SessionExpired, L10n.Text("本地会话缺失。"));
            }
            _current = _current with { Account = account };
            _store?.Save(_current);
            Changed?.Invoke();
            return LoginOutcome.Success(_current);
        }
        catch (JsonException)
        {
            return LoginOutcome.Failed(LoginFailure.MalformedResponse, L10n.Text("返回内容无法识别。"));
        }
    }

    public void Dispose() => _transport.Dispose();

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken) =>
        _delay(_schedule.DelayBefore(attempt), cancellationToken);

    private async Task<ApiReply> SendAsync(string path, string? json, string? bearer, CancellationToken cancellationToken) =>
        await SendAsync(path, json is null ? "GET" : "POST", json, bearer, cancellationToken).ConfigureAwait(false);

    private async Task<ApiReply> SendAsync(string path, string verb, string? json, string? bearer, CancellationToken cancellationToken)
    {
        var request = new ApiRequest(verb, path, json, bearer);

        // The client itself carries no timeout — HttpClient raises the same
        // TaskCanceledException for a timeout and for a user cancel, and after the
        // fact no catch clause can tell them apart. The deadline is a linked token
        // here instead, where the two are still distinguishable.
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _transport.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ApiTimeoutException(L10n.Format($"登录服务在 {_timeout.TotalSeconds.ToString("0", L10n.Culture)} 秒内没有响应。"), error);
        }
    }

    /// <summary>
    /// Reads the token payload. Every field here is required: a session without an
    /// access token, a refresh token or an expiry is not a session, and reading it
    /// as one would leave the client believing it is signed in with a credential
    /// nobody can use.
    /// <para>
    /// A field that is absent, null or of the wrong shape is a malformed payload,
    /// and it says so by raising <see cref="JsonException"/> — the exception the
    /// callers already turn into <see cref="LoginFailure.MalformedResponse"/>.
    /// </para>
    /// </summary>
    private static AuthTokens ReadTokens(JsonElement root)
    {
        var access = JsonPayload.String(root, "accessToken");
        var refresh = JsonPayload.String(root, "refreshToken");
        var expires = JsonPayload.Timestamp(root, "expiresAt");
        var account = JsonPayload.Object(root, "account");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expires is null || account is null)
            throw new JsonException("The token payload is missing a field this client needs.");
        return new AuthTokens(access, refresh, expires.Value, ReadAccount(account.Value));
    }

    /// <summary>
    /// Reads an account view from any element that has one. Pulled out of
    /// <see cref="ReadTokens"/> so the <c>/v1/account</c> read uses exactly the same
    /// field-by-field parsing as the login and refresh paths do. Two callers, one
    /// shape, no chance for them to drift if the wire ever grows a new field.
    /// <para>
    /// The identity is required; the display fields are not. A null
    /// <c>createdAt</c> or <c>lastLoginAt</c> is a pointer on the Go side that
    /// happens to be empty, and it must not take the read down with it.
    /// </para>
    /// </summary>
    private static AccountView ReadAccount(JsonElement accountElement)
    {
        if (accountElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The account payload is not an object.");
        var id = JsonPayload.String(accountElement, "id");
        if (string.IsNullOrEmpty(id))
            throw new JsonException("The account payload carries no id.");
        return new AccountView(
            id,
            JsonPayload.String(accountElement, "nickname") ?? "",
            JsonPayload.String(accountElement, "avatarUrl") ?? "",
            JsonPayload.String(accountElement, "plan") ?? "",
            JsonPayload.Timestamp(accountElement, "createdAt") ?? default,
            JsonPayload.Timestamp(accountElement, "lastLoginAt"),
            JsonPayload.Boolean(accountElement, "wechatBound"));
    }
}

/// <summary>
/// The service answered, and the answer was a refusal. Carries the server's own
/// Chinese message, because that text is written for the user and duplicating it
/// here would be a second thing to keep in step.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string message, string code, string requestId, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
        RequestId = requestId;
    }

    public int StatusCode { get; }

    /// <summary>One of <see cref="ApiErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>The service's correlation id — the first thing support should ask for.</summary>
    public string RequestId { get; }

    /// <summary>
    /// Whether repeating the call could plausibly work. A 401 is not one of them:
    /// the token is the problem, and asking again with the same token is how a user
    /// ends up believing they are being rate-limited.
    /// </summary>
    public bool IsRetryable => StatusCode is 408 or 425 or 429 || StatusCode >= 500;
}
