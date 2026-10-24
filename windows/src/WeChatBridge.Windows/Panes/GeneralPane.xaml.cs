using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace WeChatBridge.Windows.Panes;

/// <summary>通用 pane: status, retention window, version.</summary>
public partial class GeneralPane : UserControl
{
    private readonly Services.ActivationGate _gate;
    private Window? _owner;
    private bool _sessionProbed;

    /// <summary>The avatar URL currently decoded into <c>AvatarImage</c>.</summary>
    private Uri? _avatarUri;

    public GeneralPane() : this(Services.ActivationGate.Current) { }

    internal GeneralPane(Services.ActivationGate gate)
    {
        _gate = gate;
        InitializeComponent();
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = L10n.Format($"当前版本 {ShortVersion(informational)}");
        RenderLicenceState();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
        var gate = _gate;
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

        var tokens = gate.StoredTokens;
        var account = tokens is not null && tokens.IsUsableAt(DateTimeOffset.UtcNow) ? tokens.Account : null;
        SessionInfoText.Text = account is null ? string.Empty : FormatSessionLine(account);
        SessionInfoText.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;

        // Same account as the line above, so the picture and the name can never
        // disagree about who is signed in.
        RenderAccountAvatar(account);
    }

    /// <summary>
    /// The WeChat avatar beside the session line, with the nickname's first character
    /// underneath it at all times.
    /// <para>
    /// There is no failure branch on purpose. WPF's <c>Image</c> draws nothing when
    /// the download fails — not a broken-image glyph, nothing — so the letter
    /// block below is the entire fallback and needs no <c>DownloadFailed</c> handler
    /// and no visibility swap. A picture that is still loading behaves the same way,
    /// which is also why nothing here blocks on the network.
    /// </para>
    /// </summary>
    private void RenderAccountAvatar(AccountView? account)
    {
        if (account is null)
        {
            AvatarContainer.Visibility = Visibility.Collapsed;
            AvatarImage.Source = null;
            AvatarFallback.Text = string.Empty;
            _avatarUri = null;
            return;
        }

        AvatarContainer.Visibility = Visibility.Visible;
        AvatarFallback.Text = AccountAvatar.Initials(account.DisplayName);

        var uri = AccountAvatar.ResolveUri(account.AvatarUrl, ConfiguredBaseAddress());

        // This runs on every window activation too, and a fresh BitmapImage starts a
        // fresh download. The server versions the URL by content digest, so an
        // unchanged URL means an unchanged picture — keep the one already decoded
        // rather than re-fetching the same bytes every time the user alt-tabs back.
        if (uri == _avatarUri)
            return;

        _avatarUri = uri;
        AvatarImage.Source = uri is null ? null : new BitmapImage(uri);
    }

    /// <summary>
    /// The address a relative avatar path is joined onto: the same override the
    /// activation transport uses, so a dev build pointed at a local server does not
    /// resolve a relative path against the shipped host.
    /// </summary>
    private static string ConfiguredBaseAddress()
    {
        // Degrades exactly like ActivationGateway.ResolveBaseAddress(): an override
        // that is blank or is not a usable absolute URI falls back to the shipped
        // default rather than being passed on. Checking only for non-blank here
        // would leave the two paths disagreeing — the transport would recover onto
        // the default host while this one handed on an unparsable base, the avatar
        // would quietly degrade to its letter block, and the user would see a
        // missing picture with nothing to explain it.
        var configured = Environment.GetEnvironmentVariable(ActivationEndpoint.BaseAddressEnvironmentVariable);

        return !string.IsNullOrWhiteSpace(configured) &&
               Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : ActivationEndpoint.DefaultBaseAddress;
    }

    private void OpenActivation_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return;

        _gate.OpenManually(window);

        // The page may have taken the licence it was opened to fetch.
        RenderLicenceState();
    }

    private void OpenPaid_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return;

        _gate.OpenAlreadyPaid(window);

        // The page may have taken the licence it was opened to fetch.
        RenderLicenceState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _gate.Changed -= OnStateChanged;
        _gate.Changed += OnStateChanged;
        if (_owner is not null)
            _owner.Activated -= OnWindowActivated;
        _owner = Window.GetWindow(this);
        if (_owner is not null)
            _owner.Activated += OnWindowActivated;
        RenderLicenceState();

        // Re-entering the pane only re-renders local state. The server probe runs
        // once; subsequent login/profile updates arrive through Changed.
        if (!_sessionProbed)
        {
            _sessionProbed = true;
            _ = RefreshSessionAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _gate.Changed -= OnStateChanged;
        if (_owner is not null)
            _owner.Activated -= OnWindowActivated;
        _owner = null;
    }

    private void OnStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnStateChanged));
            return;
        }
        if (IsLoaded)
            RenderLicenceState();
    }

    /// <summary>
    /// Pane-level re-read on owner focus. A ShowDialog closing — LoginWindow,
    /// PurchaseWindow, or any future modal the gate opens — necessarily
    /// re-activates the owner; that's the natural moment to fetch the latest
    /// snapshot without a restart.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e) => RenderLicenceState();

    /// <summary>
    /// One network read on pane load: asks the licence service whether the
    /// cached session is still alive and who it belongs to. The result, when the
    /// server agrees, becomes the "会话：xxx · 上次活跃 yy" line under the licence
    /// status — a customer who restarts after a month should see at a glance
    /// whether the app still remembers them.
    /// <para>
    /// Network failure preserves the cached identity. A missing session or a 401
    /// hides it, and both labels are rendered from the resulting local state.
    /// </para>
    /// </summary>
    private async Task RefreshSessionAsync()
    {
        try
        {
            await _gate
                .MeAsync(CancellationToken.None)
                .ConfigureAwait(true);
            if (IsLoaded)
                RenderLicenceState();
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
        if (delta.TotalMinutes < 1) return L10n.Text("刚刚");
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
