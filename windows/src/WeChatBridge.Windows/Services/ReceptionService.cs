using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Threading;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>The lifecycle of one received archive, persisted so a restart resumes it.</summary>
public enum ReceptionJobState
{
    WaitingForSource,
    Staging,
    Queued,
    Archiving,
    Succeeded,
    Partial,
    Failed,
    Duplicate,
}

/// <summary>One received archive. This is the durable fact behind a 最近归档 row.</summary>
public sealed class ReceptionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourcePath { get; set; } = "";
    public string? StagedPath { get; set; }
    public string? ChatName { get; set; }
    public string? ContentHash { get; set; }
    public string? NotePath { get; set; }
    public string? ArchivePath { get; set; }
    public string? VaultPath { get; set; }
    public string? Subfolder { get; set; }
    public ReceptionJobState State { get; set; } = ReceptionJobState.WaitingForSource;
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? Detail { get; set; }
    public int Attempts { get; set; }
}

/// <summary>
/// A successful delivery, keyed by content and destination. It is what makes the
/// same bytes land once per vault; only written after the products commit.
/// </summary>
public sealed class ReceptionReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ContentHash { get; set; } = "";
    public string VaultPath { get; set; } = "";
    public string Subfolder { get; set; } = "";
    public string NotePath { get; set; } = "";
    public DateTimeOffset SucceededAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A file that existed when reception first started; it is history, not an import.</summary>
public sealed class BaselineFile
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long WriteTicks { get; set; }
}

/// <summary>Per-source state: first enable time and the history baseline.</summary>
public sealed class ReceptionSourceRecord
{
    public string SourceId { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public List<BaselineFile> Baseline { get; set; } = [];
}

/// <summary>What the status area's single button should do.</summary>
public enum StatusActionKind
{
    None,
    ChooseVault,
    Pause,
    Resume,
    Recheck,
    CheckDestination,
}

/// <summary>An immutable snapshot of a job, shaped for the 最近归档 list.</summary>
public sealed class ReceptionRow
{
    public ReceptionRow(ReceptionJob job) => Job = job;

    public ReceptionJob Job { get; }

    public string ChatName => string.IsNullOrWhiteSpace(Job.ChatName)
        ? Path.GetFileNameWithoutExtension(Job.SourcePath)
        : Job.ChatName!;

    public string TimeText
    {
        get
        {
            var local = Job.ObservedAt.LocalDateTime;
            return local.Date == DateTime.Now.Date
                ? local.ToString("HH:mm")
                : local.ToString("M月d日 HH:mm");
        }
    }

    public bool IsBusy => Job.State is ReceptionJobState.Staging
        or ReceptionJobState.Queued or ReceptionJobState.Archiving;

    public string StateText => Job.State switch
    {
        ReceptionJobState.WaitingForSource => "等待源文件",
        ReceptionJobState.Staging => "正在接收",
        ReceptionJobState.Queued => "等待保存",
        ReceptionJobState.Archiving => "正在保存",
        ReceptionJobState.Succeeded => "已保存",
        ReceptionJobState.Partial => "部分完成",
        ReceptionJobState.Failed => "保存失败",
        _ => "已保存",
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Job.Detail);

    public string Detail => Job.Detail ?? "";

    public string ActionText => Job.State switch
    {
        ReceptionJobState.Succeeded or ReceptionJobState.Partial => "打开笔记",
        ReceptionJobState.Failed => "重试",
        ReceptionJobState.WaitingForSource => "重新检查",
        _ => "",
    };

    public bool HasAction => ActionText.Length > 0;
}

/// <summary>
/// Owns WorkBuddy reception end to end: detect the folder, watch it, serialize
/// jobs through a single consumer, deliver into the one vault, and expose the
/// three things the minimal window shows — whether we are receiving, where it
/// saves, and what happened last.
/// </summary>
public sealed class ReceptionService : INotifyPropertyChanged, IDisposable
{
    private const string JobsFileName = "jobs.json";
    private const string ReceiptsFileName = "receipts.json";
    private const string SourcesFileName = "sources.json";

    private readonly AppSettingsStore _settingsStore;
    private readonly InboxPaths _paths;
    private readonly WorkBuddySourceProbe _probe = new();
    private readonly string _receptionDir;
    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Guid> _candidates = new();
    private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _recheckTimer;
    private readonly DispatcherTimer _scanTimer;

    private AppSettings _settings;
    private WorkBuddySourceProbeResult _source;
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private ReceptionSourceRecord? _sourceRecord;
    private List<ReceptionJob> _jobs = [];
    private List<ReceptionReceipt> _receipts = [];
    private int _recheckStep;
    private bool _destinationOk;
    private string? _destinationDetail;
    private bool _disposed;
    // Transition guards so the recheck timer (every few seconds) does not flood
    // the log: we only write a line when health first becomes (or stops being) an error.
    private WorkBuddySourceState _lastLoggedSource = WorkBuddySourceState.Checking;
    private bool? _lastLoggedDestinationOk;

    private string _statusTitle = "正在检查接收条件";
    private string _statusDetail = "";
    private string _statusActionText = "";
    private StatusActionKind _statusAction = StatusActionKind.None;

    public ReceptionService(AppSettingsStore settingsStore, InboxPaths paths)
    {
        _settingsStore = settingsStore;
        _paths = paths;
        _ui = Dispatcher.CurrentDispatcher;
        _settings = settingsStore.Load();
        _receptionDir = Path.Combine(AppLayout.Current.DataRoot, "WorkBuddyReception");
        Directory.CreateDirectory(_receptionDir);

        _source = WorkBuddySourceProbeResult.Checking(
            WorkBuddySourceResolver.DefaultDirectory, isOverride: false);

        LoadLedger();
        RebuildRows();

        _recheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _recheckTimer.Tick += (_, _) => RecheckNow();
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _scanTimer.Tick += (_, _) => ScanSource();

        _worker = Task.Run(() => WorkerAsync(_shutdown.Token));
        RecheckNow();
    }

    // MARK: - Public surface

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A short, non-blocking notice for the shell to surface (tray balloon or toast).</summary>
    public event Action<string>? NotificationRequested;

    public AppSettings Settings => _settings;

    public WorkBuddySourceProbeResult SourceProbe => _source;

    public string SourcePath => _source.ResolvedPath;

    public bool IsPaused => _settings.ReceptionPaused;

    public bool HasVault => _settings.IsVaultConfigured;

    public string VaultDisplay
    {
        get
        {
            if (!_settings.IsVaultConfigured)
                return "尚未选择知识库";
            var vault = _settings.ObsidianVaultPath!.TrimEnd('\\', '/');
            var name = Path.GetFileName(vault);
            if (string.IsNullOrEmpty(name))
                name = vault;
            return $"{name} / {_settings.ObsidianSubfolder}";
        }
    }

    public string VaultPathDisplay => _settings.ObsidianVaultPath ?? "";

    public string StatusTitle => _statusTitle;

    public string StatusDetail => _statusDetail;

    public string StatusActionText => _statusActionText;

    public StatusActionKind StatusAction => _statusAction;

    public bool HasStatusAction => _statusAction != StatusActionKind.None;

    public ObservableCollection<ReceptionRow> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public string RecentEmptyText => HasVault
        ? "还没有归档记录。在微信中选择聊天记录，合并转发到 WorkBuddy，即可自动保存。"
        : "选择知识库后，聊天记录会自动保存到这里。";

    /// <summary>Load the persisted ledger again — the shell calls this when the inbox signals.</summary>
    public void RefreshFromDisk()
    {
        LoadLedger();
        RebuildRows();
        Recompute();
    }

    /// <summary>Re-probe the source and the destination, then rebuild the watcher.</summary>
    public void RecheckNow()
    {
        _recheckTimer.Stop();
        _recheckStep = 0;

        _source = _probe.Probe(_settings.WorkBuddyDirectoryOverride);
        var (ok, detail) = DestinationProbe.Check(_settings.ObsidianVaultPath);
        _destinationOk = ok;
        _destinationDetail = detail;

        if (_settings.IsVaultConfigured && !_settings.ReceptionPaused)
        {
            if (_source.State == WorkBuddySourceState.Ready)
            {
                StartWatcher();
            }
            else
            {
                DisposeWatcher();
                ScheduleRecheck();
            }
        }
        else
        {
            DisposeWatcher();
        }

        Recompute();
    }

    public void PauseReception()
    {
        if (_settings.ReceptionPaused) return;
        _settings.ReceptionPaused = true;
        SaveSettings();
        DisposeWatcher();
        RecheckNow();
    }

    public void ResumeReception()
    {
        if (!_settings.ReceptionPaused) return;
        _settings.ReceptionPaused = false;
        SaveSettings();
        RecheckNow();
    }

    public void TogglePause()
    {
        if (_settings.ReceptionPaused) ResumeReception();
        else PauseReception();
    }

    /// <summary>Persist a new preference set and restart reception against it.</summary>
    public void ApplySettings(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        _settings = settings;
        SaveSettings();
        RecheckNow();
        RebuildRows();
    }

    /// <summary>Choose or change the vault from the first-use state.</summary>
    public void SetVault(string vaultPath, string subfolder)
    {
        _settings.ObsidianVaultPath = vaultPath;
        _settings.ObsidianSubfolder = string.IsNullOrWhiteSpace(subfolder) ? "微信聊天" : subfolder;
        ApplySettings(_settings);
    }

    public void OpenVault()
    {
        var vault = _settings.ObsidianVaultPath;
        if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault))
        {
            InboxLogger.Write(_paths, $"打开知识库失败：知识库文件夹不可用（{vault ?? "未配置"}）");
            Notify("知识库文件夹不可用，请重新选择。");
            return;
        }
        if (!OpenInObsidian(vault, null, "知识库"))
            Reveal(vault);
    }

    public void OpenNote(ReceptionRow row)
    {
        var note = row.Job.NotePath;
        if (string.IsNullOrWhiteSpace(note) || !File.Exists(note))
        {
            InboxLogger.Write(_paths, $"打开笔记失败：文件不存在（{note ?? "无路径"}）");
            Notify("笔记位置已改变，已为你打开保存文件夹。");
            Reveal(row.Job.VaultPath ?? _settings.ObsidianVaultPath ?? "");
            return;
        }
        if (!OpenInObsidian(note, row.Job.VaultPath, "笔记"))
            Reveal(Path.GetDirectoryName(note) ?? "");
    }

    /// <summary>
    /// Opens a path in Obsidian, and says so in the log either way.
    /// <para>
    /// The subtlety is that <c>obsidian://open?path=…</c> only resolves a path
    /// that already sits inside a vault Obsidian knows about. Point it at an
    /// unregistered folder and Obsidian answers with a "Vault not found"
    /// dialog: the URI launches, the process starts, and the failure never
    /// reaches this code. So the registry is consulted first, and the vault is
    /// registered when it is missing.
    /// </para>
    /// <para>
    /// Registration only reaches Obsidian's configuration file, and Obsidian
    /// reads that file at startup — a vault added while Obsidian runs is not
    /// recognised until the next start. Rather than leave the user staring at
    /// a dialog they did not cause, that case falls back to the file manager
    /// and says why.
    /// </para>
    /// </summary>
    /// <param name="target">The vault folder or note to open.</param>
    /// <param name="vaultPath">The vault the target belongs to, when known.</param>
    /// <param name="what">"知识库" or "笔记" — used in the log only.</param>
    /// <returns>True when Obsidian was asked to open the target.</returns>
    private bool OpenInObsidian(string target, string? vaultPath, string what)
    {
        var vault = vaultPath ?? _settings.ObsidianVaultPath ?? "";

        if (!ObsidianProtocol.IsRegistered())
        {
            InboxLogger.Write(_paths, $"打开{what}失败：obsidian:// 协议未注册，已回退到资源管理器。");
            Notify("未检测到 Obsidian，已为你打开文件夹。");
            return false;
        }

        var registered = ObsidianVaultRegistry.FindContaining(target)
            ?? (string.IsNullOrWhiteSpace(vault) ? null : ObsidianVaultRegistry.FindContaining(vault));

        if (registered is null)
        {
            // Not known to Obsidian. Register the vault that owns the target.
            var toRegister = string.IsNullOrWhiteSpace(vault) ? target : vault;
            if (!ObsidianVaultRegistry.Register(toRegister, out var id, out var error))
            {
                InboxLogger.Write(_paths, $"打开{what}失败：知识库注册失败（{error}），已回退到资源管理器。");
                Notify("无法在 Obsidian 中打开，已为你打开文件夹。");
                return false;
            }

            // Obsidian reads its configuration at startup. When it is not
            // running, the registration above is in place by the time the URI
            // starts it, so the vault resolves. When it is running, its
            // in-memory list predates the registration and the same URI would
            // raise the very dialog this code exists to avoid.
            if (ObsidianVaultRegistry.IsRunning())
            {
                InboxLogger.Write(_paths,
                    $"打开{what}：知识库已写入 Obsidian 注册表（id={id}），但 Obsidian 正在运行，"
                    + "需重启后才会识别，本次改为在资源管理器中打开。");
                Notify("该知识库刚添加到 Obsidian，重启 Obsidian 后即可直接在 Obsidian 中打开；现在先为你打开文件夹。");
                return false;
            }

            registered = new ObsidianVaultEntry(id!, toRegister);
        }

        // Inside a registered vault: vault + file is the documented form and
        // does not depend on how Obsidian resolves an absolute path.
        var uri = string.IsNullOrWhiteSpace(vault)
            ? $"obsidian://open?vault={Uri.EscapeDataString(registered.Id)}"
            : BuildNoteUri(registered.Id, vault, target);

        if (!TryStartUri(uri, out var failure))
        {
            InboxLogger.Write(_paths, $"打开{what}失败：无法启动协议（{failure}），已回退到资源管理器。");
            Notify("无法在 Obsidian 中打开，已为你打开文件夹。");
            return false;
        }

        InboxLogger.Write(_paths, $"打开{what}：{uri}（目标 {target}）");
        return true;
    }

    /// <summary>
    /// Builds an <c>obsidian://open?vault=…&file=…</c> URI for a file inside
    /// a vault. The file is expressed relative to the vault root, the way
    /// Obsidian's own "Copy Obsidian URL" does it.
    /// </summary>
    private static string BuildNoteUri(string vaultId, string vaultPath, string notePath)
    {
        var root = vaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, notePath);
        }
        catch (Exception error) when (error is ArgumentException or PathTooLongException)
        {
            relative = notePath;
        }
        var file = string.Join('/',
            relative.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return $"obsidian://open?vault={Uri.EscapeDataString(vaultId)}&file={file}";
    }

    public void Retry(ReceptionRow row)
    {
        if (_settings.ReceptionPaused || !_settings.IsVaultConfigured) return;
        var id = row.Job.Id;
        _ui.Invoke(() =>
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return;
            job.State = job.StagedPath is not null && File.Exists(job.StagedPath)
                ? ReceptionJobState.Queued
                : ReceptionJobState.WaitingForSource;
            job.Detail = null;
            job.UpdatedAt = DateTimeOffset.Now;
            SaveJobs();
            RebuildRows();
            Recompute();
        });
        _candidates.Enqueue(id);
        try { _queueSignal.Release(); } catch (ObjectDisposedException) { }
    }

    // MARK: - Detection, watching, scanning

    private void StartWatcher()
    {
        DisposeWatcher();
        if (!_settings.IsVaultConfigured || _settings.ReceptionPaused
            || _source.State != WorkBuddySourceState.Ready)
            return;
        try
        {
            var watcher = new FileSystemWatcher(_source.ResolvedPath, "*.zip")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = false,
            };
            watcher.Created += (_, e) => EnqueueFromEvent(e.FullPath);
            watcher.Renamed += (_, e) => EnqueueFromEvent(e.FullPath);
            watcher.Changed += (_, e) => EnqueueFromEvent(e.FullPath);
            watcher.Error += (_, e) => OnWatcherError(e.GetException());
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _scanTimer.Start();
            ScanSource();
        }
        catch (Exception error) when (error is IOException or ArgumentException
            or UnauthorizedAccessException or NotSupportedException)
        {
            _source = _source with { State = WorkBuddySourceState.WatchUnavailable, Detail = error.Message };
            ScheduleRecheck();
        }
    }

    private void OnWatcherError(Exception error)
    {
        InboxLogger.Write(_paths, "WorkBuddy 监听中断", error);
        _ui.BeginInvoke(new Action(() =>
        {
            _source = _source with { State = WorkBuddySourceState.WatchUnavailable, Detail = error.Message };
            DisposeWatcher();
            ScheduleRecheck();
            Recompute();
        }));
    }

    private void ScheduleRecheck()
    {
        // 2, 5, 15, 30 seconds, then every 30 — back off rather than spin, but
        // never stop trying: the folder may be created a minute after launch.
        var seconds = _recheckStep switch { 0 => 2, 1 => 5, 2 => 15, _ => 30 };
        _recheckStep = Math.Min(_recheckStep + 1, 3);
        _recheckTimer.Interval = TimeSpan.FromSeconds(seconds);
        _recheckTimer.Start();
    }

    /// <summary>
    /// Enumerate candidates. The first time a source is seen, snapshot it as
    /// history and enqueue nothing — opening the app must not dump every old ZIP
    /// into the vault. Later starts enqueue files that are new or changed.
    /// </summary>
    private void ScanSource()
    {
        if (_source.State != WorkBuddySourceState.Ready
            || !_settings.IsVaultConfigured || _settings.ReceptionPaused)
            return;

        if (_sourceRecord is null
            || !string.Equals(_sourceRecord.Path, _source.ResolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            var record = new ReceptionSourceRecord
            {
                SourceId = StableId(_source.ResolvedPath),
                Path = _source.ResolvedPath,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Baseline = [],
            };
            foreach (var file in SafeEnumerate())
            {
                var snapshot = Snapshot(file);
                if (snapshot is not null)
                    record.Baseline.Add(snapshot);
            }
            _sourceRecord = record;
            SaveSourceRecord();
            return;
        }

        foreach (var file in SafeEnumerate())
        {
            if (!IsBaselineUnchanged(file))
                EnqueueFromEvent(file);
        }
    }

    private IEnumerable<string> SafeEnumerate()
    {
        try
        {
            return Directory.EnumerateFiles(_source.ResolvedPath, "*.zip", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private bool IsBaselineUnchanged(string path)
    {
        var entry = _sourceRecord?.Baseline.FirstOrDefault(
            b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        var snapshot = Snapshot(path);
        if (snapshot is null) return true;
        return snapshot.Length == entry.Length && snapshot.WriteTicks == entry.WriteTicks;
    }

    private static BaselineFile? Snapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new BaselineFile { Path = info.FullName, Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks }
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void EnqueueFromEvent(string path)
    {
        if (_disposed || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        if (!_pending.TryAdd(path, 0)) return;

        Guid id;
        try
        {
            id = _ui.Invoke(() =>
            {
                if (_settings.ReceptionPaused || !_settings.IsVaultConfigured)
                    return Guid.Empty;
                var existing = _jobs.FirstOrDefault(j =>
                    string.Equals(j.SourcePath, path, StringComparison.OrdinalIgnoreCase)
                    && j.State is not (ReceptionJobState.Succeeded or ReceptionJobState.Duplicate));
                if (existing is not null)
                {
                    existing.State = ReceptionJobState.WaitingForSource;
                    existing.UpdatedAt = DateTimeOffset.Now;
                    SaveJobs();
                    RebuildRows();
                    return existing.Id;
                }
                var job = new ReceptionJob
                {
                    SourcePath = path,
                    ChatName = Path.GetFileNameWithoutExtension(path),
                    State = ReceptionJobState.WaitingForSource,
                    VaultPath = _settings.ObsidianVaultPath,
                    Subfolder = _settings.ObsidianSubfolder,
                };
                _jobs.Add(job);
                SaveJobs();
                RebuildRows();
                Recompute();
                return job.Id;
            });
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, "登记 WorkBuddy 记录失败", error);
            _pending.TryRemove(path, out _);
            return;
        }

        if (id == Guid.Empty)
        {
            _pending.TryRemove(path, out _);
            return;
        }
        _candidates.Enqueue(id);
        try { _queueSignal.Release(); } catch (ObjectDisposedException) { }
    }

    // MARK: - Worker

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!_candidates.TryDequeue(out var id))
                continue;
            try
            {
                await ProcessAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                InboxLogger.Write(_paths, "WorkBuddy 归档任务失败", error);
                var source = await OnUiAsync(() => _jobs.FirstOrDefault(j => j.Id == id)?.SourcePath);
                if (source is not null)
                    _pending.TryRemove(source, out _);
            }
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        var setup = await OnUiAsync<(ReceptionJob Job, string Vault, string Subfolder)?>(() =>
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || job.State is ReceptionJobState.Succeeded or ReceptionJobState.Duplicate)
                return null;
            if (_settings.ReceptionPaused || !_settings.IsVaultConfigured)
                return null;
            var vault = _settings.ObsidianVaultPath!;
            var subfolder = _settings.ObsidianSubfolder;
            job.Attempts++;
            job.State = ReceptionJobState.Staging;
            job.UpdatedAt = DateTimeOffset.Now;
            SaveJobs();
            RebuildRows();
            Recompute();
            return (job, vault, subfolder);
        }).ConfigureAwait(false);

        if (setup is null) return;
        var job = setup.Value.Job;

        var sourcePath = job.StagedPath is { Length: > 0 } stagedSource && File.Exists(stagedSource)
            ? stagedSource
            : job.SourcePath;

        // A staged copy is already complete; only an unstaged source needs the
        // stability wait. This is why a retry works after WorkBuddy moved the file.
        var isStagedRetry = !string.Equals(sourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase);
        if (!isStagedRetry)
        {
            var stability = await WaitStableAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (stability != Stability.Stable)
            {
                await UpdateJobAsync(id, j =>
                {
                    j.State = ReceptionJobState.WaitingForSource;
                    j.Detail = stability == Stability.Gone
                        ? "源文件已移走，请重新导出。"
                        : "等待源文件写入完成。";
                }).ConfigureAwait(false);
                return;
            }
        }

        var staged = Path.Combine(_receptionDir, "staging", id.ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        string hash;
        try
        {
            hash = await CopyAndHashAsync(sourcePath, staged, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            await UpdateJobAsync(id, j =>
            {
                j.State = ReceptionJobState.Failed;
                j.Detail = "源文件已移走，请重新导出。";
            }).ConfigureAwait(false);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await UpdateJobAsync(id, j =>
            {
                j.State = ReceptionJobState.WaitingForSource;
                j.Detail = "等待源文件写入完成。";
            }).ConfigureAwait(false);
            return;
        }

        var duplicate = await OnUiAsync(() => _receipts.FirstOrDefault(r =>
            r.ContentHash == hash
            && string.Equals(Normalize(r.VaultPath), Normalize(setup.Value.Vault), StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Subfolder, setup.Value.Subfolder, StringComparison.Ordinal))).ConfigureAwait(false);

        if (duplicate is not null)
        {
            await OnUiAsync(() =>
            {
                var target = _jobs.FirstOrDefault(j => j.Id == id);
                if (target is not null)
                {
                    target.ContentHash = hash;
                    target.StagedPath = staged;
                    target.NotePath = duplicate.NotePath;
                    target.State = ReceptionJobState.Duplicate;
                    target.Detail = "这条记录已经保存过。";
                    target.UpdatedAt = DateTimeOffset.Now;
                    SaveJobs();
                    RebuildRows();
                    Recompute();
                }
                return true;
            }).ConfigureAwait(false);
            return;
        }

        await OnUiAsync(() =>
        {
            var target = _jobs.FirstOrDefault(j => j.Id == id);
            if (target is null) return false;
            target.ContentHash = hash;
            target.StagedPath = staged;
            target.State = ReceptionJobState.Archiving;
            target.Detail = null;
            target.UpdatedAt = DateTimeOffset.Now;
            SaveJobs();
            RebuildRows();
            return true;
        }).ConfigureAwait(false);

        try
        {
            var result = await Task.Run(() => KnowledgeDelivery.DeliverResult(
                [staged], setup.Value.Vault, setup.Value.Subfolder, chatName: null, sceneName: null,
                cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

            var note = result.Notes.FirstOrDefault();
            var archive = result.Archives.FirstOrDefault();
            var warning = result.Warnings.Count > 0 ? string.Join("；", result.Warnings) : null;
            var succeeded = result.Status == KnowledgeDelivery.DeliveryStatus.Complete;

            await OnUiAsync(() =>
            {
                var target = _jobs.FirstOrDefault(j => j.Id == id);
                if (target is null) return false;
                target.NotePath = note;
                target.ArchivePath = archive;
                target.Detail = warning;
                target.State = succeeded ? ReceptionJobState.Succeeded : ReceptionJobState.Partial;
                target.UpdatedAt = DateTimeOffset.Now;
                if (succeeded)
                {
                    _receipts.Add(new ReceptionReceipt
                    {
                        ContentHash = hash,
                        VaultPath = Normalize(setup.Value.Vault),
                        Subfolder = setup.Value.Subfolder,
                        NotePath = note ?? "",
                        SucceededAt = DateTimeOffset.Now,
                    });
                    SaveReceipts();
                }
                SaveJobs();
                RebuildRows();
                Recompute();
                return true;
            }).ConfigureAwait(false);

            if (succeeded && _settings.NotifyOnArchiveSuccess)
                Notify("聊天记录已保存到知识库。");
            else if (!succeeded)
                Notify("部分内容未能保存，可在列表中查看原因。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, "保存聊天记录失败", error);
            await UpdateJobAsync(id, j =>
            {
                j.State = ReceptionJobState.Failed;
                j.Detail = error.Message;
            }).ConfigureAwait(false);
        }
        finally
        {
            var source = job.SourcePath;
            _pending.TryRemove(source, out _);
        }
    }

    private async Task UpdateJobAsync(Guid id, Action<ReceptionJob> mutate)
    {
        await OnUiAsync(() =>
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return false;
            mutate(job);
            job.UpdatedAt = DateTimeOffset.Now;
            SaveJobs();
            RebuildRows();
            Recompute();
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The 750 ms sample: the same length and write time twice, non-empty, and
    /// openable exclusively — the last one is the only proof WorkBuddy closed it.
    /// </summary>
    private static async Task<Stability> WaitStableAsync(string path, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        var previousWrite = DateTime.MinValue;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return Stability.Gone;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Stability.Gone;
            }

            if (info.Length != previousLength || info.LastWriteTimeUtc != previousWrite)
            {
                previousLength = info.Length;
                previousWrite = info.LastWriteTimeUtc;
                continue;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (stream.Length == 0) continue;
                return Stability.Stable;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }
        return Stability.TimedOut;
    }

    private static async Task<string> CopyAndHashAsync(string source, string destination, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        sha.TransformFinalBlock(buffer, 0, 0);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(sha.Hash!);
    }

    // MARK: - Status composition

    private void Recompute()
    {
        var active = _jobs.Where(j => j.State is ReceptionJobState.Staging
            or ReceptionJobState.Queued or ReceptionJobState.Archiving).ToList();
        var lastSuccess = _jobs.Where(j => j.State is ReceptionJobState.Succeeded or ReceptionJobState.Partial)
            .OrderByDescending(j => j.UpdatedAt).FirstOrDefault();

        if (!_settings.IsVaultConfigured)
        {
            Set("选择聊天记录的保存位置",
                "选择你的 Obsidian 知识库；微信里转发给 WorkBuddy 的记录会自动保存到其中。",
                "选择知识库", StatusActionKind.ChooseVault);
        }
        else if (_settings.ReceptionPaused)
        {
            Set("已暂停接收",
                "继续接收后会补收暂停期间的新记录。",
                "继续接收", StatusActionKind.Resume);
        }
        else if (!_destinationOk)
        {
            Set("无法保存到知识库",
                string.IsNullOrWhiteSpace(_destinationDetail)
                    ? "已接收的记录会保留，修复后继续保存。"
                    : _destinationDetail + "（已接收的记录会保留，修复后继续保存。）",
                "检查保存位置", StatusActionKind.CheckDestination);
        }
        else if (active.Count > 0)
        {
            var name = active[0].ChatName ?? Path.GetFileNameWithoutExtension(active[0].SourcePath);
            var waiting = active.Count - 1;
            Set("正在保存聊天记录",
                waiting > 0 ? $"{name}（另有 {waiting} 条等待）" : name,
                "暂停接收", StatusActionKind.Pause);
        }
        else
        {
            switch (_source.State)
            {
                case WorkBuddySourceState.Missing:
                    Set("等待 WorkBuddy 导出聊天记录",
                        "在微信中选择记录并转发到 WorkBuddy。",
                        "重新检测", StatusActionKind.Recheck);
                    break;
                case WorkBuddySourceState.AccessDenied:
                    Set("无法接收聊天记录",
                        "没有读取来源目录的权限，可在设置中手动指定。",
                        "重新检测", StatusActionKind.Recheck);
                    break;
                case WorkBuddySourceState.WatchUnavailable:
                    Set("无法接收聊天记录",
                        "来源目录暂时无法监听，正在自动重试。",
                        "重新检测", StatusActionKind.Recheck);
                    break;
                case WorkBuddySourceState.Ready when lastSuccess is not null:
                    Set("正在接收微信聊天",
                        $"最近保存：{lastSuccess.UpdatedAt.LocalDateTime:HH:mm}",
                        "暂停接收", StatusActionKind.Pause);
                    break;
                default:
                    Set("等待微信聊天记录",
                        "接收目录已就绪。",
                        "暂停接收", StatusActionKind.Pause);
                    break;
            }
        }

        RecordHealthLogging();
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(HasVault));
        OnPropertyChanged(nameof(VaultDisplay));
        OnPropertyChanged(nameof(VaultPathDisplay));
        OnPropertyChanged(nameof(RecentEmptyText));
        OnPropertyChanged(nameof(SourcePath));
    }

    private void Set(string title, string detail, string actionText, StatusActionKind action)
    {
        _statusTitle = title;
        _statusDetail = detail;
        _statusActionText = actionText;
        _statusAction = action;
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(StatusActionText));
        OnPropertyChanged(nameof(StatusAction));
        OnPropertyChanged(nameof(HasStatusAction));
    }

    /// <summary>
    /// Mirror source/destination health into the log, but only on a transition
    /// into (or out of) an error state. The recheck timer and recompute fire
    /// constantly, so logging every pass would bury real failures under noise.
    /// </summary>
    private void RecordHealthLogging()
    {
        if (IsSourceError(_source.State))
        {
            if (_lastLoggedSource != _source.State)
            {
                InboxLogger.Write(_paths,
                    $"来源目录异常（{_source.State}）：{_source.ResolvedPath} | {_source.Detail ?? "无详情"}");
            }
        }
        else if (IsSourceError(_lastLoggedSource))
        {
            InboxLogger.Write(_paths, $"来源目录已恢复：{_source.State}（{_source.ResolvedPath}）");
        }
        _lastLoggedSource = _source.State;

        // A missing vault is "not configured yet", not a write failure — only
        // log an actual write probe failure once the user has chosen a vault.
        if (_settings.IsVaultConfigured && !_destinationOk)
        {
            if (_lastLoggedDestinationOk != false)
                InboxLogger.Write(_paths, $"知识库不可写：{_destinationDetail ?? "未知原因"}");
        }
        else if (_lastLoggedDestinationOk == false && _destinationOk)
        {
            InboxLogger.Write(_paths, "知识库写入已恢复。");
        }
        _lastLoggedDestinationOk = _destinationOk;
    }

    private static bool IsSourceError(WorkBuddySourceState state) =>
        state is WorkBuddySourceState.AccessDenied or WorkBuddySourceState.WatchUnavailable;

    // MARK: - Ledger

    private void LoadLedger()
    {
        _jobs = ConfigStore.Load<List<ReceptionJob>>(_receptionDir, JobsFileName) ?? [];
        _receipts = ConfigStore.Load<List<ReceptionReceipt>>(_receptionDir, ReceiptsFileName) ?? [];
        _sourceRecord = ConfigStore.Load<ReceptionSourceRecord>(_receptionDir, SourcesFileName);
        // A job interrupted mid-flight is not a success. Put it back at the front
        // of the queue instead of leaving a spinner that never resolves.
        foreach (var job in _jobs.Where(j => j.State is ReceptionJobState.Staging
            or ReceptionJobState.Queued or ReceptionJobState.Archiving))
        {
            job.State = job.StagedPath is not null && File.Exists(job.StagedPath)
                ? ReceptionJobState.Queued
                : ReceptionJobState.WaitingForSource;
            job.Detail = "上次运行中断，已重新排队。";
        }
    }

    private void SaveJobs() => ConfigStore.Save(_receptionDir, JobsFileName, _jobs);

    private void SaveReceipts() => ConfigStore.Save(_receptionDir, ReceiptsFileName, _receipts);

    private void SaveSourceRecord()
    {
        if (_sourceRecord is not null)
            ConfigStore.Save(_receptionDir, SourcesFileName, _sourceRecord);
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
        // settings.json is the truth; obsidian.json is a projection the Core
        // delivery path still reads. Keep it in step instead of letting a stale
        // copy decide the destination.
        try
        {
            new ObsidianSettingsStore().Save(new ObsidianSettings
            {
                VaultPath = _settings.ObsidianVaultPath,
                Subfolder = _settings.ObsidianSubfolder,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            InboxLogger.Write(_paths, "同步 obsidian.json 失败", error);
        }
    }

    private void RebuildRows()
    {
        if (!_ui.CheckAccess())
        {
            _ui.BeginInvoke(new Action(RebuildRows));
            return;
        }
        Recent.Clear();
        foreach (var job in _jobs
            .Where(j => j.State != ReceptionJobState.Duplicate)
            .OrderByDescending(j => j.ObservedAt)
            .ThenByDescending(j => j.UpdatedAt)
            .Take(50))
        {
            Recent.Add(new ReceptionRow(job));
        }
        OnPropertyChanged(nameof(HasRecent));
    }

    // MARK: - Helpers

    private enum Stability { Stable, TimedOut, Gone }

    private Task OnUiAsync(Action action)
    {
        if (_ui.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _ui.InvokeAsync(action).Task;
    }

    private Task<T> OnUiAsync<T>(Func<T> func) => _ui.CheckAccess()
        ? Task.FromResult(func())
        : _ui.InvokeAsync(func).Task;

    private void Notify(string message)
    {
        if (!_ui.CheckAccess())
        {
            _ui.BeginInvoke(new Action(() => NotificationRequested?.Invoke(message)));
            return;
        }
        NotificationRequested?.Invoke(message);
    }

    /// <summary>
    /// Starts a URI through the shell.
    /// <para>
    /// The failure reason is returned rather than swallowed: the caller logs
    /// it, and a launch that fails for an unknown reason is the difference
    /// between "Obsidian was not installed" and "Obsidian said no".
    /// </para>
    /// </summary>
    private static bool TryStartUri(string uri, out string? failure)
    {
        failure = null;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            failure = error.Message;
            return false;
        }
    }

    private static void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing else to try; a failed reveal must not take the app down.
        }
    }

    private static string Normalize(string path) => path.TrimEnd('\\', '/');

    private static string StableId(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private void DisposeWatcher()
    {
        _scanTimer.Stop();
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Already gone.
        }
        _watcher = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recheckTimer.Stop();
        DisposeWatcher();
        _shutdown.Cancel();
        _queueSignal.Dispose();
        _shutdown.Dispose();
        _ = _worker;
    }
}
