using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows.Panes;

/// <summary>通用 pane: status, retention window, version.</summary>
public partial class GeneralPane : UserControl
{
    public GeneralPane()
    {
        InitializeComponent();
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = L10n.Format($"当前版本 {ShortVersion(informational)}");
        RenderLicenceState();
        // Fires once when the pane is first added to the visual tree; we read the
        // session then. Subsequent re-entries do not re-probe — a stale "上次活跃"
        // is honest, a hammering probe is not, and the user can always restart if
        // they want a fresh read.
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Shows where this installation's licence stands.
    /// <para>
    /// Reads the gate rather than binding to it: the snapshot resolves on first read
    /// and is cached after, so this is cheap enough to call from
    /// <see cref="Loaded"/> as well as the constructor — a portable stick inserted
    /// after startup changes the answer.
    /// </para>
    /// </summary>
    private void RenderLicenceState()
    {
        var gate = Services.ActivationGate.Current;
        LicenceStatusText.Text = gate.StatusText;

        var installId = gate.Snapshot.InstallId;
        var showMachineCode = !string.IsNullOrWhiteSpace(installId)
            && gate.Snapshot.State is not (Core.Activation.GateState.Portable or Core.Activation.GateState.Licensed);
        MachineCodeHint.Visibility = showMachineCode ? Visibility.Visible : Visibility.Collapsed;
        MachineCodeHint.Text = showMachineCode ? L10n.Format($"本机机器码：{installId}") : string.Empty;

        // Nothing to open for a licensed machine or a portable stick, so the button
        // goes rather than opening a window that says "you are already done".
        OpenActivationButton.Visibility = gate.CanOpenFromSettings ? Visibility.Visible : Visibility.Collapsed;
        OpenPaidButton.Visibility = gate.CanOpenFromSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenActivation_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return;

        Services.ActivationGate.Current.OpenManually(window);

        // The page may have taken the licence it was opened to fetch.
        RenderLicenceState();
    }

    private void OpenPaid_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return;

        Services.ActivationGate.Current.OpenAlreadyPaid(window);

        // The page may have taken the licence it was opened to fetch.
        RenderLicenceState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Fire-and-forget is safe here: RefreshSessionAsync swallows everything
        // except OperationCanceledException, and a missing-session 401 lands as
        // LoginFailure.SessionExpired rather than an unobserved exception.
        _ = RefreshSessionAsync();
    }

    /// <summary>
    /// One network read on pane load: asks the licence service whether the
    /// cached session is still alive and who it belongs to. The result, when the
    /// server agrees, becomes the "会话：xxx · 上次活跃 yy" line under the licence
    /// status — a customer who restarts after a month should see at a glance
    /// whether the app still remembers them.
    /// <para>
    /// Network failure, no local session, and a 401 all leave the line hidden.
    /// That is the quietest behaviour that does not lie: a failed probe is not a
    /// "you're signed out", and the existing UI already says the right thing for
    /// the unsigned-out case via <see cref="RenderLicenceState"/>.
    /// </para>
    /// </summary>
    private async Task RefreshSessionAsync()
    {
        try
        {
            var outcome = await Services.ActivationGate.Current
                .MeAsync(CancellationToken.None)
                .ConfigureAwait(true);
            if (!outcome.Succeeded || outcome.Tokens is null)
                return;

            SessionInfoText.Text = FormatSessionLine(outcome.Tokens.Account);
            SessionInfoText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // Closing the pane during the read is a legitimate path.
        }
        catch (Exception ex)
        {
            // A logged failure on a "still signed in?" probe is not worth a crash
            // and not worth user-facing noise. The cached session is still on disk;
            // the next launch will probe again.
            Debug.WriteLine($"general pane: me refresh failed — {ex.Message}");
        }
    }

    private static string FormatSessionLine(AccountView account)
    {
        var line = L10n.Format($"会话：{account.DisplayName}");
        if (account.LastLoginAt is { } at)
            // RelativeTime returns a string already; the " · " separator stays
            // outside Format because the relative phrasing is opaque to the
            // formatter and there is no value in rebuilding it twice.
            line += " · " + RelativeTime(at);
        return line;
    }

    private static string RelativeTime(DateTimeOffset past)
    {
        // Rough and locale-aware via L10n: anything coarser than a minute gets
        // rounded down, never up, so "5 分钟前" stays honest past the 60-second
        // boundary. Anything past two weeks is "很久之前" — exact day counts on a
        // last-login time are noise the customer did not ask for.
        var delta = DateTimeOffset.UtcNow - past;
        if (delta.TotalMinutes < 1) return "刚刚";
        if (delta.TotalMinutes < 60) return L10n.Format($"{(int)delta.TotalMinutes} 分钟前");
        if (delta.TotalHours < 24) return L10n.Format($"{(int)delta.TotalHours} 小时前");
        if (delta.TotalDays < 14) return L10n.Format($"{(int)delta.TotalDays} 天前");
        return L10n.Text("很久之前");
    }

    /// <summary>1.0.0+commithash → 1.0.0 (short hash) — same rule as AboutPane.</summary>
    private static string ShortVersion(string? informational)
    {
        if (string.IsNullOrEmpty(informational))
            return "0.1.0";
        var plus = informational.IndexOf('+');
        if (plus < 0)
            return informational;
        var hash = informational[(plus + 1)..Math.Min(plus + 8, informational.Length)];
        return $"{informational[..plus]} ({hash})";
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void RevealInbox_Click(object sender, RoutedEventArgs e) => Model?.RevealInbox();

    /// <summary>重新运行设置向导 — macOS SettingsActions.restartOnboarding.</summary>
    private void RestartOnboarding_Click(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.RestartOnboarding();

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/freestylefly/WeChatBridge",
                UseShellExecute = true,
            });
        }
        catch
        {
            // A missing browser is not worth an error dialog in settings.
        }
    }
}
