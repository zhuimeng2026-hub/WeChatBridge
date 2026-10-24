using System.Text.Json;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>Why an activation call did not finish a purchase.</summary>
public enum ActivationFailure
{
    None,

    /// <summary>The service could not be reached. Repeating is free: registration is idempotent per ticket.</summary>
    Unreachable,

    /// <summary>The seller declined. The registration stays on record so it can be looked at later.</summary>
    Rejected,

    /// <summary>The deadline passed. The registration is still pending server-side.</summary>
    TimedOut,

    /// <summary>The licence came back but does not verify against the embedded key.</summary>
    LicenseUnverifiable,

    /// <summary>The licence is bound to a different install id than this machine's.</summary>
    SubjectMismatch,

    /// <summary>The response could not be read.</summary>
    MalformedResponse,

    /// <summary>Not signed in, so there is no account to buy against.</summary>
    NotSignedIn,

    /// <summary>This machine has no install identity, so there is nothing to bind a licence to.</summary>
    NoInstallId,

    /// <summary>
    /// The service holds the licence but cannot hand it over yet (HTTP 409): the
    /// seller confirmed and the signature is still being produced. A pause, not
    /// a failure — the next poll resolves it.
    /// <para>
    /// This is the one 409 the client is meant to keep waiting on. Reading it as
    /// a success would tell a customer they own the software when no envelope
    /// was ever written; reading it as a failure would end a purchase that is
    /// one poll away from done.
    /// </para>
    /// </summary>
    LicenceNotReady,

    /// <summary>
    /// The service refused because of the current state (HTTP 409) on a call that
    /// does not read a licence: on registration, this machine already carries a
    /// live request belonging to another account.
    /// </summary>
    Conflict,
}

/// <summary>What one activation call produced.</summary>
public sealed record ActivationOutcome(
    ActivationFailure Failure,
    bool CanRetry,
    string Detail,
    LicenseEnvelope? License = null,
    DateTimeOffset? IssuedAt = null,
    string? RequestId = null)
{
    /// <summary>
    /// Whether this machine holds a licence.
    /// <para>
    /// Not "whether the call went well": a registration the server recorded and a
    /// status poll that found the seller still waiting are both perfectly good
    /// calls, and neither of them has produced a licence. Only the envelope that
    /// was verified and written counts, so the shape here is the same as
    /// <see cref="LoginOutcome.Succeeded"/>: no error <i>and</i> the thing that was
    /// asked for.
    /// </para>
    /// </summary>
    public bool Succeeded => Failure == ActivationFailure.None && License is not null;

    /// <summary>
    /// Whether the seller has confirmed. Only a status poll can set it, and it is the
    /// cue to go and collect the licence — which is a separate call, and one the
    /// service can still refuse while the signature is being produced.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// Whether a registration is actually on the server's ledger.
    /// <para>
    /// Distinct from <see cref="Succeeded"/>: a registration is not a licence, and
    /// only the registration has a request id to poll. A caller that treats "no
    /// error" as "licensed" would tell a customer they own the software when
    /// nothing was written to disk; a caller that treats "no error" as "registered"
    /// would poll for an order that does not exist, and the user would sit there
    /// watching a spinner for an hour.
    /// </para>
    /// </summary>
    public bool IsRegistered => RequestId is not null;

    public static ActivationOutcome Success(LicenseEnvelope license, DateTimeOffset? issuedAt, string detail) =>
        new(ActivationFailure.None, false, detail, license, issuedAt);

    /// <summary>
    /// The call did its job and there is no error, but there is no licence either:
    /// the registration is on the server's ledger, or the seller has not confirmed
    /// yet. Neither is a failure, and neither is <see cref="Succeeded"/>.
    /// </summary>
    public static ActivationOutcome InProgress(string detail, string? requestId = null) =>
        new(ActivationFailure.None, true, detail, RequestId: requestId);

    public static ActivationOutcome Failed(ActivationFailure failure, bool canRetry, string detail, string? requestId = null) =>
        new(failure, canRetry, detail, RequestId: requestId);
}

/// <summary>
/// The buy-out purchase: register this machine, wait for the seller to confirm a
/// payment, collect the licence, verify it offline, keep it.
/// <para>
/// <b>The order of operations in <see cref="Collect"/> is the load-bearing part.</b>
/// The licence is decoded, verified against the embedded public key, and checked
/// against this machine's install id <i>before</i> it is written to disk — in that
/// order, with no <c>await</c> between the check and the write. A licence that
/// verifies but binds to someone else's machine must never reach
/// <see cref="LicenseStore"/>, because everything downstream treats a file there as
/// a purchase; and a licence that does not verify must never be stored even once, or
/// the user is left with an app that says no and a file it cannot delete.
/// </para>
/// <para>
/// <b>No payment goes through this class.</b> The design is deliberate: the client
/// shows the seller's static payment code, the buyer scans it in WeChat, and the
/// seller checks their own 微信零账. There is no payment API to integrate, no money
/// this code can move, and no order it can lose.
/// </para>
/// </summary>
public sealed class ActivationService
{
    private readonly LicenseStore _licenses;
    private readonly LicenseVerifier _verifier;
    private readonly IActivationTransport _transport;
    private readonly AccountService _account;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;

    public ActivationService(
        LicenseStore licenses,
        LicenseVerifier verifier,
        IActivationTransport transport,
        AccountService account,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        _licenses = licenses ?? throw new ArgumentNullException(nameof(licenses));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _timeout = timeout ?? ActivationEndpoint.DefaultTimeout;
        _log = log;
    }

    /// <summary>
    /// Registers this machine for a purchase.
    /// <para>
    /// <paramref name="anchorDigest"/> is the hardware anchor's digest, which is
    /// evidence for the <i>seller</i> — it is how a re-install is told apart from a
    /// different machine. It is not the primary key and cannot be: MAC addresses
    /// change with the network card, and a licence that stopped working because
    /// someone moved machines would punish exactly the customer the product is
    /// supposed to serve. The binding stays on <paramref name="installId"/>.
    /// </para>
    /// </summary>
    public async Task<ActivationOutcome> RegisterAsync(
        string installId,
        string anchorDigest,
        int? amountFen,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installId))
            return ActivationOutcome.Failed(ActivationFailure.NoInstallId, false, L10n.Text("本机尚未生成机器码，无法登记购买。"));
        var token = RequireToken();
        if (token is null)
            return ActivationOutcome.Failed(ActivationFailure.NotSignedIn, false, L10n.Text("请先登录微信账号。"));

        var json = JsonSerializer.Serialize(
            new { installId, anchorDigest, amountFen }, ActivationJson.Options);
        var reply = await SendAsync(ActivationEndpoint.ActivationRequestPath, json, token, cancellationToken)
            .ConfigureAwait(false);

        if (!reply.IsSuccess)
            return Classify(reply, L10n.Text("登记购买失败，请稍后重试。"));

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            // The request id is the only thing this client can poll on, so a
            // reply without one is not a registration anybody can use.
            var requestId = JsonPayload.String(document.RootElement, "requestId");
            if (string.IsNullOrEmpty(requestId))
                throw new JsonException("The registration answer carries no request id.");
            _log?.Invoke($"activation: registered {requestId} for install {installId}");
            return ActivationOutcome.InProgress(L10n.Format($"已登记，授权码 {requestId}，等待卖家确认。"), requestId);
        }
        catch (JsonException)
        {
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, L10n.Text("登记结果无法识别，请稍后重试。"));
        }
    }

    /// <summary>One status poll. Never polls by itself.</summary>
    public async Task<ActivationOutcome> CheckStatusAsync(CancellationToken cancellationToken)
    {
        var token = RequireToken();
        if (token is null)
            return ActivationOutcome.Failed(ActivationFailure.NotSignedIn, false, L10n.Text("请先登录微信账号。"));

        var reply = await SendAsync(ActivationEndpoint.ActivationStatusPath, null, token, cancellationToken)
            .ConfigureAwait(false);

        if (reply.StatusCode == 404)
        {
            // No registration at all. The mock and the real handler agree that this
            // is a "not found", and for a user it means the same thing: nothing is
            // in flight, so the purchase page should offer to register.
            return ActivationOutcome.InProgress(L10n.Text("还没有购买登记。"));
        }

        if (!reply.IsSuccess)
            return Classify(reply, L10n.Text("查询授权状态失败，请稍后重试。"));

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            // The status is the whole answer here: without it there is nothing to
            // wait for and nothing to collect.
            var status = JsonPayload.String(root, "status");
            if (string.IsNullOrEmpty(status))
                throw new JsonException("The status answer carries no status.");
            var licenseId = JsonPayload.String(root, "licenseId");

            if (string.Equals(status, ActivationState.Confirmed, StringComparison.Ordinal))
                return ActivationOutcome.InProgress(L10n.Text("卖家已确认，正在取许可证…")) with { Confirmed = true };
            if (string.Equals(status, ActivationState.Rejected, StringComparison.Ordinal))
                return ActivationOutcome.Failed(ActivationFailure.Rejected, false,
                    L10n.Text("卖家拒绝了本次登记。如已付款，请联系客服。"));

            return ActivationOutcome.InProgress(L10n.Format($"等待卖家确认（许可证 {licenseId ?? L10n.Text("待签发")}）。"));
        }
        catch (JsonException)
        {
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, L10n.Text("授权状态无法识别，请稍后重试。"));
        }
    }

    /// <summary>
    /// Collects the licence the seller issued, verifies it, and writes it to disk.
    /// <para>
    /// A 409 here means confirmed-but-not-yet-readable, which is a real state: the
    /// handler signs on confirm and the read can lose that race. It is treated as
    /// retryable rather than as an error, because the next poll resolves it — and
    /// it is emphatically not a licence: no envelope was read, so nothing was
    /// written, and saying otherwise would tell a customer they own the software
    /// when they do not.
    /// </para>
    /// </summary>
    public async Task<ActivationOutcome> CollectAsync(string? installId, CancellationToken cancellationToken)
    {
        var token = RequireToken();
        if (token is null)
            return ActivationOutcome.Failed(ActivationFailure.NotSignedIn, false, L10n.Text("请先登录微信账号。"));

        var reply = await SendAsync(ActivationEndpoint.ActivationLicencePath, null, token, cancellationToken)
            .ConfigureAwait(false);

        if (reply.StatusCode == 409)
        {
            var conflict = ApiFailure.TryParse(reply.Body);
            return ActivationOutcome.Failed(ActivationFailure.LicenceNotReady, true,
                conflict?.Message ?? L10n.Text("许可证正在签发，请稍候。"));
        }
        if (!reply.IsSuccess)
            return Classify(reply, L10n.Text("获取许可证失败，请稍后重试。"));

        LicenseEnvelope? envelope;
        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var payload = JsonPayload.Object(document.RootElement, "envelope")
                ?? throw new JsonException("The licence answer carries no envelope.");
            envelope = payload.Deserialize<LicenseEnvelope>(ActivationJson.Options);
        }
        catch (JsonException)
        {
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, L10n.Text("许可证内容无法识别，请稍后重试。"));
        }

        if (envelope is null)
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, L10n.Text("许可证内容无法识别，请稍后重试。"));

        return Accept(envelope, installId);
    }

    /// <summary>
    /// Verify, then write. No <c>await</c> between the two, and no ordering other
    /// than this one — see the class comment for why.
    /// </summary>
    private ActivationOutcome Accept(LicenseEnvelope envelope, string? installId)
    {
        var now = _clock();
        var verification = _verifier.Verify(envelope, now, LicenseRuntimeOptions.Installed.ClockSkewTolerance);
        if (!verification.IsValid)
        {
            _log?.Invoke($"activation: licence refused — {verification.Failure}");
            return ActivationOutcome.Failed(ActivationFailure.LicenseUnverifiable, true,
                L10n.Text("许可证验签未通过，请联系客服并提供单号。"));
        }

        if (!string.IsNullOrWhiteSpace(installId) &&
            !string.Equals(envelope.Subject, installId, StringComparison.Ordinal))
        {
            // Reached only when the signature is genuine, which means the seller
            // signed for a different machine. Worth saying out loud: it is the one
            // case where the client is certain the service is right and the local
            // machine is the odd one out.
            _log?.Invoke($"activation: licence subject {envelope.Subject} is not this machine ({installId})");
            return ActivationOutcome.Failed(ActivationFailure.SubjectMismatch, false,
                L10n.Text("这张许可证属于另一台机器，请联系客服换机重签。"));
        }

        _licenses.Save(envelope);
        _log?.Invoke($"activation: licence {envelope.Subject} written");
        return ActivationOutcome.Success(envelope, envelope.IssuedAt, L10n.Text("授权成功，感谢购买。"));
    }

    private string? RequireToken()
    {
        var token = _account.BearerToken;
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static ActivationOutcome Classify(ApiReply reply, string fallback)
    {
        var failure = ApiFailure.TryParse(reply.Body);
        var code = failure?.Code ?? ApiErrorCodes.Internal;
        var retryable = reply.StatusCode is 408 or 425 or 429 || reply.StatusCode >= 500;
        var detail = failure?.Message ?? $"{fallback}（HTTP {reply.StatusCode}）";

        // A conflict is the service refusing because of the current state, not
        // because anything went wrong. It used to be classified as "no error",
        // which made the caller read a refusal as a purchase: no licence was
        // ever written, and the customer was told they owned the software.
        var kind = code switch
        {
            ApiErrorCodes.Unauthorized => ActivationFailure.NotSignedIn,
            ApiErrorCodes.Conflict => ActivationFailure.Conflict,
            _ => retryable ? ActivationFailure.Unreachable : ActivationFailure.MalformedResponse,
        };
        return ActivationOutcome.Failed(kind, retryable, detail);
    }

    private async Task<ApiReply> SendAsync(string path, string? json, string bearer, CancellationToken cancellationToken)
    {
        var verb = json is null ? "GET" : "POST";

        // No client timeout: HttpClient's own raises the same TaskCanceledException
        // as the user pressing cancel, and no catch clause can tell them apart
        // afterwards. The deadline lives here, where the two still can.
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var request = new ApiRequest(verb, path, json, bearer);
        return await _transport.SendAsync(request, linked.Token).ConfigureAwait(false);
    }
}
