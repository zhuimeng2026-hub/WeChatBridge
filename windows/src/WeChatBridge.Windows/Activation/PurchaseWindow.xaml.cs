using System.Windows;
using System.Windows.Input;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Activation;

/// <summary>
/// The buy-out purchase window: a shell around <see cref="ActivationController"/>.
/// <para>
/// Same shape as <see cref="LoginWindow"/> — no logic beyond rendering, one
/// <c>Render()</c> driven by the controller's <c>Changed</c> event, so there is never a
/// second button handler quietly enabling something.
/// </para>
/// <para>
/// The payment code and the seller's contact details are not in this app, and the
/// app carries no state of its own any more. WeChat's redirect lands in a browser,
/// so the page that came back from the scan is the only place the code exists —
/// and it prints the machine code, the amount and the contact beside it. Keeping a
/// second copy here meant two surfaces to replace together, and this one was still
/// an empty placeholder while the real one worked.
/// </para>
/// <para>
/// What is left here is the part that needs a real window: which machine is being
/// licensed, and the 「我已付款」 action that registers it.
/// </para>
/// </summary>
public partial class PurchaseWindow : Window
{
    private readonly ActivationController _controller;

    public PurchaseWindow(ActivationController controller, bool autoRegister = false)
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

        // First paint. Without this the XAML defaults — empty machine code, an
        // empty status line, a paid button hidden behind Visibility="Collapsed" —
        // sit on screen until the auto-register path's first Raise() reaches Render,
        // which is the "blank window" frame a freshly-returned customer notices
        // before the controller pushes its first state.
        Render();

        // When opened from the "我已付款" entry, the click already asserted payment,
        // so ask for the authorization code (the request id) the moment the window
        // appears instead of waiting for a second press.
        if (autoRegister)
            Loaded += (_, _) => _ = RegisterAsync();
    }

    /// <summary>True when the licence is on disk, so the gate can be re-read.</summary>
    public bool IsLicensed => _controller.IsLicensed;

    private void OnPaidClicked(object sender, RoutedEventArgs e) => _ = RegisterAsync();

    private void OnRetryClicked(object sender, RoutedEventArgs e) => _ = RegisterAsync();

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _controller.Cancel();
        Render();
    }

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCopyInstallId(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_controller.InstallId);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Someone else owns the clipboard. The code is on screen and selectable;
            // failing to copy is not worth an error box over.
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private async Task RegisterAsync()
    {
        await _controller.RegisterAsync();
        Render();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _controller.Changed -= Render;

        // Cancel, not Dispose: the controller may still be mid-await on a poll, and
        // disposing a CancellationTokenSource out from under it is a race. It is
        // disposed by whoever created it.
        _controller.Cancel();
    }

    /// <summary>The only place this window decides what it looks like.</summary>
    private void Render()
    {
        // The controller raises Changed from its poll loop, which runs on the captured
        // context but is not guaranteed to be the dispatcher once a retry has bounced
        // through an await. Touching named elements from anything but the UI thread
        // throws, so every write goes through here.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Render);
            return;
        }

        InstallIdBox.Text = _controller.InstallId;
        AnchorDigestText.Text = string.IsNullOrWhiteSpace(_controller.AnchorDigest)
            ? L10n.Text("（无法读取，仅以机器码为准）")
            : _controller.AnchorDigest;

        // The transfer note is the machine code: it is what the seller matches a manual
        // WeChat transfer against, and the only link between a payment and a licence.
        // The browser page prints it too; this is the copy the buyer can take from.
        PaymentNoteText.Text = _controller.InstallId;

        StatusText.Text = _controller.StatusMessage;

        var stage = _controller.Stage;

        DetailText.Visibility = _controller.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        DetailText.Text = _controller.CanRetry
            ? L10n.Text("登记没有丢失，可以直接重试。")
            : string.Empty;

        FinishButton.Visibility = stage == PurchaseStage.Done ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _controller.IsPolling ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _controller.CanRetry ? Visibility.Visible : Visibility.Collapsed;

        // Once the registration is in flight the only honest button is the one that
        // stops the wait, so 停止等待 and 我已付款 are never both on screen.
        PaidButton.Visibility =
            stage == PurchaseStage.Idle ? Visibility.Visible : Visibility.Collapsed;

        // Once the money is moving there is nothing to confirm or claim; the only
        // honest button is the one that stops the wait.
        FooterNote.Visibility = _controller.IsPolling ? Visibility.Collapsed : Visibility.Visible;
    }
}
