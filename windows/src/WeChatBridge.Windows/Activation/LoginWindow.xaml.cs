using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Activation;

/// <summary>
/// The WeChat sign-in window: a shell around <see cref="AccountController"/>.
/// <para>
/// No logic lives here beyond rendering what the controller says. The state comes
/// from the controller's <c>Changed</c> event and is applied by the single
/// <see cref="Render"/> at the bottom — one place where the window's appearance is
/// decided, so there is never a second button handler quietly enabling something.
/// </para>
/// <para>
/// <b>What the user sees.</b> A button to get a sign-in link, a clickable link that
/// opens the system browser on the URL the server hands back, a status line, and a
/// countdown until the ticket expires. The QR code used to live here; the server
/// renders it as a plain link that WeChat opens inside the phone browser, which is
/// not how PC sign-in works, so the image is gone.
/// </para>
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AccountController _controller;

    /// <summary>
    /// The window auto-closes itself once sign-in is recorded. The flag keeps the
    /// auto-close attempt idempotent — a successful Stage=Done raises Render, but
    /// the same window can also be torn down by the user pressing × or 「完成」,
    /// and a second BeginInvoke(Close) past a half-closed window is the kind of
    /// thing that turns "login worked, jump to purchase" into a null-ref.
    /// </summary>
    private bool _autoCloseStarted;

    public LoginWindow(AccountController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();

        // Clamped the way every dialog here is: a fixed-size window on a small display
        // otherwise hangs off the bottom of the work area, with the button that
        // dismisses it off-screen.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);

        _controller.Changed += Render;
        Closed += OnClosed;

        // First paint. The button starts disabled in XAML; without this call it
        // stays disabled until the controller happens to raise Changed, which on
        // a quiet Idle stage is never — the "button does nothing" bug.
        Render();
    }

    /// <summary>True when the user closed the window without finishing, not 取消.</summary>
    public bool IsSignedIn => _controller.Stage == LoginStage.Done;

    private void OnStartClicked(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private async Task StartAsync()
    {
        await _controller.StartAsync();
        Render();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _controller.Changed -= Render;

        // Cancel, not Dispose: the controller may still be mid-await on a poll, and
        // disposing a CancellationTokenSource out from under it is a race. It is
        // disposed by whoever created it.
        _controller.Cancel();
    }

    /// <summary>
    /// Opens the system browser on the link the user just clicked. The pattern is
    /// the same one <c>MainViewModel</c> uses to launch an <c>obsidian://</c> URL:
    /// <c>UseShellExecute = true</c> is required so .NET hands the URL to the
    /// default browser instead of trying to fetch it itself.
    /// </summary>
    private void OnAuthorizeClicked(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            // Step out of the way: the user is now looking at a browser tab, not this
            // window. We come back automatically when the controller flips to Done.
            // If the launch above threw, we skip the minimise so the user can still
            // see the error and try the Copy button instead.
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            // Surface a friendly hint without crashing the window. The status line
            // will get refreshed on the next controller tick.
            System.Diagnostics.Debug.WriteLine($"login: failed to open {e.Uri} ({ex.Message})");
        }
    }

    /// <summary>
    /// Copies the sign-in URL to the clipboard verbatim. The link is the
    /// <i>same</i> one the <c>AuthorizeLink</c> would have launched, so a user whose
    /// default browser is wedged, or whose link handler has been hijacked by some
    /// other app, can paste it into whatever they trust.
    /// </summary>
    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var url = _controller.AuthorizeUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Clipboard.SetText(url);
            StatusText.Text = L10n.Text("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            // Clipboard.SetText can throw on locked-down shells; fall through to a
            // hint that asks the user to select the link manually. Don't crash.
            System.Diagnostics.Debug.WriteLine($"login: clipboard set failed ({ex.Message})");
            StatusText.Text = L10n.Text("复制失败，请手动选中链接");
        }
    }

    /// <summary>The only place this window decides what it looks like.</summary>
    private void Render()
    {
        // AccountService polls without capturing the UI context. Its progress
        // callback must return to the dispatcher before touching WPF controls.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Render));
            return;
        }

        StatusText.Text = _controller.StatusMessage;

        var url = _controller.AuthorizeUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            AuthorizeText.Visibility = Visibility.Collapsed;
            AuthorizeLink.NavigateUri = null;
            AuthorizeRun.Text = " ";
        }
        else
        {
            AuthorizeText.Visibility = Visibility.Visible;
            AuthorizeLink.NavigateUri = new Uri(url, UriKind.Absolute);
            AuthorizeRun.Text = url;
        }

        CountdownText.Text = _controller.SecondsRemaining > 0
            ? L10n.Format($"登录链接剩余 {_controller.SecondsRemaining} 秒")
            : L10n.Text("登录链接有效期 5 分钟");

        var done = _controller.Stage == LoginStage.Done;
        FinishButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled = _controller.CanStart;

        // CopyButton follows the link: enabled when the URL is on screen, disabled
        // otherwise. The button is in the XAML tree the whole time, so its position
        // does not jump as the controller advances.
        CopyButton.IsEnabled = !string.IsNullOrEmpty(_controller.AuthorizeUrl);

        // If we minimised ourselves so the user could see the browser, come back
        // when the controller reports Done. The WindowState guard covers the
        // window the user never asked us to minimise: it stays Normal and is
        // never activated.
        //
        // It does NOT cover a window the user minimised by hand. The guard
        // cannot tell "we minimised ourselves to get out of the way" from
        // "the user put us away", so a hand-minimised window is still pulled
        // back to Normal and activated on Done — the one case the user asked
        // not to be interrupted. Distinguishing the two needs a flag set
        // where we minimise ourselves; until that exists, the comment here
        // must not claim otherwise.
        if (done && WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Activate();
        }

        // The button doubles as 刷新 while a link is on screen: a ticket that has been
        // open for five minutes and never scanned is expired, and the user's next move
        // is to get a new one.
        StartButton.Content = _controller.IsWaiting
            ? L10n.Text("换一个登录链接")
            : _controller.CanRetry
                ? L10n.Text("重试")
                : L10n.Text("获取登录链接");

        // The "刚刚登上,还要手动点完成" bug. Once the controller records a successful
        // sign-in the bearer token is on disk and the next thing the customer needs to
        // do is in another window; leaving this one up means the user has to know to
        // click 「完成」, and a user who clicks × instead walks away thinking nothing
        // happened (the ShowDialog returns and the purchase window silently never
        // opens). Auto-close one dispatcher tick after Done paints, so the
        // "已登录：xxx" status is visible for one frame and then the flow moves.
        if (done && !_autoCloseStarted)
        {
            _autoCloseStarted = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DialogResult = true;
                Close();
            }));
        }
    }
}
