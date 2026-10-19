using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Activation;

/// <summary>What the login window is currently showing.</summary>
public enum LoginStage
{
    /// <summary>Nothing has been asked for yet.</summary>
    Idle,

    /// <summary>Asking the service for a sign-in link.</summary>
    Requesting,

    /// <summary>Showing the link and waiting for the phone.</summary>
    WaitingScan,

    /// <summary>Signed in.</summary>
    Done,

    /// <summary>Something went wrong and the user has a button to press.</summary>
    Failed,
}

/// <summary>
/// The login flow as a state machine, with no WPF type in sight.
/// <para>
/// The window owns no logic, subscribes to <see cref="Changed"/>, and renders
/// whatever this says. The alternative — an <c>async void</c> click handler that
/// awaits three calls and then pokes at named controls — makes the interesting part
/// (what happens when the poll fails, or the ticket expires mid-wait) untestable and
/// unresumable.
/// </para>
/// <para>
/// <b>Cancellation is terminal.</b> Closing the window cancels the wait and that
/// <see cref="CancellationTokenSource"/> is never reused, so a second attempt starts
/// from a clean ticket rather than inheriting a half-finished one.
/// </para>
/// <para>
/// <b>The QR image is gone.</b> The server's <c>/qr</c> endpoint renders a code that
/// WeChat opens as a plain link in the phone browser, which is not how PC sign-in
/// works. The window now shows the <see cref="AuthorizeUrl"/> as a clickable link
/// the user opens in a desktop browser; the code that lives inside that page is the
/// one WeChat will accept. See <c>docs/design/client-login-browser-fix.zh-CN.md</c>.
/// </para>
/// </summary>
public sealed class AccountController : IDisposable
{
    private readonly AccountService _account;
    private readonly string? _installId;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AccountController(AccountService account, string? installId, Action<string>? log = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _installId = installId;
        _log = log;
    }

    public event Action? Changed;

    public LoginStage Stage { get; private set; } = LoginStage.Idle;

    /// <summary>One line, in the user's language, saying what is happening.</summary>
    public string StatusMessage { get; private set; } = L10n.Text("点击下方按钮获取登录链接。");

    /// <summary>
    /// The URL to open in a desktop browser. WeChat renders the real sign-in code
    /// inside that page; showing the URL is the only way the user can reach it.
    /// </summary>
    public string AuthorizeUrl { get; private set; } = string.Empty;

    /// <summary>Seconds left on the ticket, for the countdown under the link.</summary>
    public int SecondsRemaining { get; private set; }

    public bool IsBusy => Stage is LoginStage.Requesting;

    /// <summary>The window is waiting on a phone, so the only useful button is 换一个.</summary>
    public bool IsWaiting => Stage == LoginStage.WaitingScan;

    public bool CanStart => !IsBusy && Stage is not LoginStage.WaitingScan;

    public bool CanRetry => Stage == LoginStage.Failed;

    public bool IsSignedIn => _account.IsSignedIn;

    /// <summary>
    /// Asks for a sign-in ticket and waits for the scan.
    /// <para>
    /// One method rather than three, because the order is not the caller's to choose:
    /// there is nothing to show without the ticket and a caller that could interleave
    /// the steps would eventually ask for a ticket twice.
    /// </para>
    /// </summary>
    public async Task StartAsync()
    {
        if (_disposed)
            return;

        AuthorizeUrl = string.Empty;
        SecondsRemaining = 0;
        Stage = LoginStage.Requesting;
        StatusMessage = L10n.Text("正在获取登录链接…");
        Raise();

        try
        {
            var ticket = await _account
                .BeginLoginAsync(_installId, Environment.MachineName, _lifetime.Token)
                .ConfigureAwait(true);

            AuthorizeUrl = ticket.AuthorizeUrl;
            Stage = LoginStage.WaitingScan;
            StatusMessage = L10n.Text("请在浏览器中打开以下链接，用微信扫码完成授权。");
            UpdateCountdown(ticket.ExpiresAt);
            Raise();

            var outcome = await _account
                .WaitForScanAsync(ticket, Remaining(ticket.ExpiresAt), OnPolled, _lifetime.Token)
                .ConfigureAwait(true);

            Stage = outcome.Succeeded ? LoginStage.Done : LoginStage.Failed;
            StatusMessage = outcome.Succeeded
                ? L10n.Format($"已登录：{outcome.Tokens!.Account.DisplayName}")
                : outcome.Detail;
            Raise();
        }
        catch (OperationCanceledException)
        {
            // The window closed. There is nobody left to tell.
        }
        catch (ApiTimeoutException error)
        {
            Fail(error.Message);
        }
        catch (ApiTransportException error)
        {
            Fail(L10n.Format($"没能联系上登录服务：{error.Message}"));
        }
        catch (ApiException error)
        {
            Fail(error.Message);
        }
    }

    /// <summary>Discards the current ticket and starts again. Same as <see cref="StartAsync"/>.</summary>
    public Task RetryAsync() => StartAsync();

    /// <summary>
    /// Ends the wait. Cooperative and final — the token is not reset, so a later
    /// call gets a fresh one.
    /// </summary>
    public void Cancel()
    {
        if (!_lifetime.IsCancellationRequested)
            _lifetime.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Refreshes the countdown on every poll tick. Called from the poll loop, so it
    /// is the only thing that updates between polls.
    /// </summary>
    private void OnPolled(SessionPoll poll)
    {
        if (poll.IsPending && poll.ExpiresAt is { } expires)
        {
            UpdateCountdown(expires);
            Raise();
        }
    }

    private void UpdateCountdown(DateTimeOffset expiresAt) =>
        SecondsRemaining = (int)Math.Max(0, Math.Round((expiresAt - DateTimeOffset.UtcNow).TotalSeconds));

    private TimeSpan Remaining(DateTimeOffset expiresAt)
    {
        var left = expiresAt - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void Fail(string message)
    {
        _log?.Invoke($"account: {message}");
        Stage = LoginStage.Failed;
        StatusMessage = message;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
