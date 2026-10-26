using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Onboarding;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows;

public partial class App : Application
{
    public App()
    {
        L10n.Language = new AppSettingsStore().Load().Language;
        WireErrorLogging();
    }

    // Every machine-wide name gets the layout suffix. A portable copy and an
    // installed copy on the same machine would otherwise silently evict each
    // other: the second launch creates its Inbox, loses the single-instance
    // mutex, and exits with no window and no message. Installed builds keep an
    // empty suffix, so their names are unchanged.
    private static readonly string Suffix = AppLayout.Current.CoordinationSuffix;
    private static string MutexName => "Local\\WeChatBridge.Windows.Main" + Suffix;
    private static string ChangeEventName => "Local\\WeChatBridge.Windows.InboxChanged" + Suffix;
    /// <summary>
    /// A second *interactive* launch asks for the window. Kept separate from
    /// <see cref="ChangeEventName"/>: a share must never pull our window to the
    /// front — the delivery engine is busy foregrounding the target app, and a
    /// window that grabs focus mid-paste steals the Ctrl+V.
    /// </summary>
    private static string ForegroundEventName => "Local\\WeChatBridge.Windows.Foreground" + Suffix;
    /// <summary>
    /// The helper's "a batch is staged, the pick is still open" signal — the
    /// cue to start the scene reads inside the user's decision time. Distinct
    /// from <see cref="ChangeEventName"/>: that one means "committed, reload".
    /// </summary>
    private static string PrefetchEventName => PrefetchHint.EventName + Suffix;

    /// <summary>How long the inbox relay waits after an I/O failure before retrying.</summary>
    private static readonly TimeSpan RelayRetryDelay = TimeSpan.FromSeconds(2);

    private Mutex? _mutex;
    private EventWaitHandle? _changeEvent;
    private EventWaitHandle? _foregroundEvent;
    private EventWaitHandle? _prefetchEvent;
    private EventWaitHandle? _collectionImportEvent;
    private CancellationTokenSource? _shutdown;
    private InboxPaths? _paths;
    private MainViewModel? _model;
    private TrayIconService? _tray;
    private ReceptionService? _reception;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        _paths = new InboxPaths();
        // Attached before anything else so that every later failure — including
        // the ones in startup itself — has somewhere to go.
        DiagnosticSink.Attach(_paths);
        // A portable build's Inbox lives on a removable drive, which can be
        // read-only, full, or mid-rename. Directory.CreateDirectory throws for
        // all three, and letting it escape OnStartup means no window and no
        // explanation — the same "nothing happened" the mutex path below used
        // to produce. Say what is wrong instead.
        try
        {
            _paths.EnsureCreated();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L10n.Text("无法在 {0} 创建数据目录。请检查磁盘是否已插入、是否可写，或换一个目录再试。"),
                    _paths.Root) + "\n\n" + error.Message,
                L10n.Text("律档通"), MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // A share that arrives while we are running spawns a second
            // --background instance (the helper always launches us). It must
            // exit quietly — foregrounding belongs to interactive launches
            // only, otherwise every share would steal focus mid-delivery.
            if (!background)
                SignalExistingInstance();
            Shutdown();
            return;
        }

        // Debris from a helper that was killed mid-copy. Swept by the owning instance
        // only, and it runs long after any such copy would have died. Matches the macOS
        // app, which prunes on start for the same reason.
        _paths.PruneStaging();

        _changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ChangeEventName);
        _foregroundEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ForegroundEventName);
        _prefetchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PrefetchEventName);
        _collectionImportEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CollectionImportProgress.EventName);
        _shutdown = new CancellationTokenSource();
        var settingsStore = new AppSettingsStore();
        // Load once here so a migrated settings file is written back even when
        // the user never opens the settings dialog.
        _ = settingsStore.Load();
        _reception = new ReceptionService(settingsStore, _paths);
        settingsStore.Save(_reception.Settings);
        _reception.NotificationRequested += message => _model?.ShowToast(message);

        // The environment snapshot comes before anything else can fail, so a
        // later error line can be read against the machine it happened on.
        // The user's machine is unknown to us; this is what makes the log
        // worth more than "it did not work".
        DiagnosticsService.WriteSnapshot(_paths, _reception.Settings);

        var model = new MainViewModel(_paths);
        _model = model;
        var window = new MainWindow(_reception);
        MainWindow = window;
        // The tray exists in every instance — it is the resident process's
        // only chrome when the window is hidden.
        _tray = new TrayIconService(model, () => window, Shutdown, _reception);
        // Closing the window hides it to the tray; the share relay must stay
        // resident, so only the tray's 退出 really ends the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // The share helper launches us with --background purely so the fresh
        // intent is consumed and the forward runs. Showing the window here
        // would fight the target app for the foreground — and the guide must
        // never pop mid-share either.
        if (!background)
        {
            window.Show();
            // The licence layer is resolved once, here, rather than lazily by
            // whichever surface asks first: a purchased licence should already
            // be in force before anything reads it, and a resolution that fails
            // belongs in the log next to startup.
            var licensing = LicensingBootstrap.Current;
            LogStartup($"licensing: edition={licensing.EditionName} pro={licensing.IsPro} " +
                $"activation={licensing.Resolution.Activation} detail={licensing.Resolution.Detail}");
        }
        // MainWindow's inbox load rides the Loaded event — a hidden window
        // never raises it, so a background launch must load explicitly or it
        // would idle forever with intents unconsumed (seen live 2026-09-25:
        // helper relaunched us hidden and two shares were never delivered).
        Dispatcher.BeginInvoke(() =>
        {
            // A hint can predate this process — the share that launched us
            // staged while we were still starting. Draining it here starts
            // the scene reads before Reload announces the batch.
            model.RefreshImportProgress();
            model.ConsumePrefetchHint();
            model.RebuildEntries();
            window.RefreshBatches();
        });
        _ = WaitForChangesAsync(_shutdown.Token);
    }

    /// <summary>
    /// Writes a startup line to the Inbox log, and nowhere else.
    /// <para>
    /// The guard is not defensive noise: a delegate that throws because a nullable
    /// field happens to be null would turn "could not write the log" into a startup
    /// crash.
    /// </para>
    /// </summary>
    private void LogStartup(string message)
    {
        if (_paths is not null)
            InboxLogger.Write(_paths, message);
    }

    /// <summary>
    /// Catch errors that would otherwise vanish: a UI-thread fault that tears the
    /// window down, a background-thread exception that kills the process, or an
    /// unobserved Task that fails silently. Every one is written to the Inbox log
    /// so "运行时出错" is recorded even when the user never sees a dialog.
    /// <para>
    /// UI-thread exceptions are marked handled so a single transient glitch (a
    /// binding or click fault) does not drop the whole resident session — the log
    /// keeps the evidence. A fatal non-UI fault cannot be undone; we log it and
    /// let the runtime end the process.
    /// </para>
    /// </summary>
    private void WireErrorLogging()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            DiagnosticLog.Error(_paths ?? new InboxPaths(), "FATAL 未处理的 UI 异常", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var error = e.ExceptionObject as Exception;
            DiagnosticLog.Error(_paths ?? new InboxPaths(),
                $"FATAL 未处理的线程异常（IsTerminating={e.IsTerminating}）", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticLog.Error(_paths ?? new InboxPaths(), "未观测的 Task 异常", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// 通用页's 重新运行设置向导 — the macOS SettingsActions.restartOnboarding
    /// port. A finished guide restarts at the top; the completion flag stays
    /// so the next launch does not think the app was never introduced.
    /// </summary>
    public void RestartOnboarding()
    {
        var store = new OnboardingStateStore();
        var state = store.Load();
        if (state.Step != 0)
        {
            state.Step = 0;
            store.Save(state);
        }
        if (_model is not null)
            new OnboardingWindow(_model, store) { Owner = MainWindow }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _reception?.Dispose();
        _model?.DisposeServices();
        _shutdown?.Cancel();
        _changeEvent?.Dispose();
        _foregroundEvent?.Dispose();
        _prefetchEvent?.Dispose();
        _collectionImportEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private async Task WaitForChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signalled = await Task.Run(
                    () => WaitHandle.WaitAny(new WaitHandle?[] { _changeEvent, _foregroundEvent, _prefetchEvent, _collectionImportEvent }
                        .OfType<WaitHandle>().ToArray(), 500),
                    cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                if (signalled == WaitHandle.WaitTimeout)
                {
                    if (_model?.ImportProgress is not null) Dispatcher.Invoke(() => _model.RefreshImportProgress());
                    continue;
                }

                Dispatcher.Invoke(() =>
                {
                    // A prefetch hint touches no window — the resident process
                    // is expected to be hidden while it runs the reads.
                    if (signalled == 3)
                    {
                        _model?.RefreshImportProgress();
                        return;
                    }
                    if (signalled == 2)
                    {
                        _model?.ConsumePrefetchHint();
                        return;
                    }
                    if (MainWindow is not MainWindow window)
                        return;
                    window.RefreshBatches();
                    // Only an explicit second launch pulls the window forward —
                    // the share helper's inbox signal just refreshes the list.
                    if (signalled == 1)
                        window.BringToFront();
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                if (_paths is not null)
                    InboxLogger.Write(_paths, L10n.Text("监听 Inbox 失败"), error);
                // Keep relaying. A removable drive makes transient errors routine
                // — a slow read, an indexer holding a handle, the volume being
                // re-enumerated — and one of them used to end the share relay for
                // the life of the process. Back off so a permanently broken root
                // does not spin, and so a drive that comes back recovers on its own.
                try { await Task.Delay(RelayRetryDelay, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ForegroundEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
