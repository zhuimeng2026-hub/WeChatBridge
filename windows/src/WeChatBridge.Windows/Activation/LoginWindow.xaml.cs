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
/// <see cref="Render"/> at the bottom, which is the same shape as <c>RedeemWindow</c>
/// — one place where the window's appearance is decided, so there is never a second
/// button handler quietly enabling something.
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
        }
        catch (Exception ex)
        {
            // Surface a friendly hint without crashing the window. The status line
            // will get refreshed on the next controller tick.
            System.Diagnostics.Debug.WriteLine($"login: failed to open {e.Uri} ({ex.Message})");
        }
    }

    /// <summary>The only place this window decides what it looks like.</summary>
    private void Render()
    {
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

        // The button doubles as 刷新 while a link is on screen: a ticket that has been
        // open for five minutes and never scanned is expired, and the user's next move
        // is to get a new one.
        StartButton.Content = _controller.IsWaiting
            ? L10n.Text("换一个登录链接")
            : _controller.CanRetry
                ? L10n.Text("重试")
                : L10n.Text("获取登录链接");
    }
}
