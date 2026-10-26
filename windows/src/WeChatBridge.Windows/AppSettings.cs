using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// App-level preferences that macOS keeps in UserDefaults. Stored as
/// %LOCALAPPDATA%\WeChatBridge\Config\settings.json through
/// <see cref="ConfigStore"/> so a corrupt file reads as defaults rather than
/// breaking the share flow.
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// A week, matching macOS <c>Preferences.defaultHistoryRetentionDays</c>:
    /// long enough that last Friday's export is still there on Monday, short
    /// enough that the inbox does not quietly become an archive.
    /// </summary>
    public const int DefaultHistoryRetentionDays = 7;

    /// <summary>The retention choices the settings pane offers; 0 means 永久保留.</summary>
    public static readonly IReadOnlyList<int> RetentionChoices = [0, 7, 30];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Share-menu entries the user switched off, by <see cref="ShareActions.RawValue"/>.
    /// Absent means enabled — a fresh install offers every entry.
    /// </summary>
    public List<string> DisabledEntries { get; set; } = [];

    /// <summary>0 means keep history forever.</summary>
    public int HistoryRetentionDays { get; set; } = DefaultHistoryRetentionDays;

    /// <summary>The Obsidian vault 沉淀到 Obsidian writes into.</summary>
    public string? ObsidianVaultPath { get; set; }

    /// <summary>Folder under the vault the chat Markdown and original ZIP land in.</summary>
    /// <remarks>
    /// New installs use 微信聊天; an upgraded settings file keeps whatever it had
    /// (see <see cref="Migrate"/>), so an existing vault layout never moves.
    /// </remarks>
    public string ObsidianSubfolder { get; set; } = "微信聊天";
    public bool AutoArchiveWorkBuddyToObsidian { get; set; } = true;
    public string Language { get; set; } = "system";
    public bool OpenObsidianAfterDelivery { get; set; }
    /// <summary>Import newly-created chat ZIPs from WorkBuddy's local export folder.</summary>
    public bool WorkBuddyAutoImport { get; set; } = true;

    /// <summary>
    /// A user-chosen source directory. Empty means "auto": the current user's
    /// documented WorkBuddy export folder. A value here is only ever set by the
    /// user; detection never writes it.
    /// </summary>
    public string? WorkBuddyDirectoryOverride { get; set; }

    /// <summary>
    /// Reception is paused and must stay paused across restarts. New installs
    /// start receiving; an upgrade from a settings file that had auto-import
    /// switched off becomes paused rather than silently starting to receive.
    /// </summary>
    public bool ReceptionPaused { get; set; }

    /// <summary>Successful-archive notifications only; failures stay visible in the list.</summary>
    public bool NotifyOnArchiveSuccess { get; set; } = true;

    public string? DeliveryFolderPath { get; set; }
    public string DeliverySubfolder { get; set; } = "律档通";

    public string? LastCollectionTarget { get; set; }
    public List<string> RecentCollectionFolders { get; set; } = [];
    public string? LastCollectionScene { get; set; }

    /// <summary>
    /// Keep a hidden instance resident after sign-in. A share's latency is
    /// dominated by cold-starting this process; resident, the helper's signal
    /// is consumed and forwarded in under a second.
    /// </summary>
    public bool LaunchAtLogin { get; set; } = true;

    /// <summary>
    /// The buy-out activation page has been offered on this install.
    /// <para>
    /// "Offered", not "declined" and not "bought". The gate locks nothing, so the only
    /// thing repeated nagging costs is the customer's willingness to read the one
    /// page; the 通用 pane keeps a button for anyone who changes their mind, which is
    /// where an upsell belongs after the first showing.
    /// </para>
    /// </summary>
    public bool ActivationGateShown { get; set; }

    public bool IsEntryEnabled(ShareAction action) =>
        !DisabledEntries.Contains(action.RawValue());

    /// <summary>True once a vault has been chosen; the minimal window keys its first-use state off this.</summary>
    public bool IsVaultConfigured => !string.IsNullOrWhiteSpace(ObsidianVaultPath);

    /// <summary>
    /// Brings an older settings file up to the current schema. Never moves the
    /// vault or the subfolder: an existing user's layout is data, not a default.
    /// </summary>
    public AppSettings Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
            return this;
        if (SchemaVersion < 2)
        {
            // The old auto-import switch carried the same intent as 暂停接收:
            // switched off meant "not now", not "never".
            ReceptionPaused = !WorkBuddyAutoImport;
        }
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }

    public void SetEntryEnabled(ShareAction action, bool enabled)
    {
        var raw = action.RawValue();
        if (enabled)
            DisabledEntries.Remove(raw);
        else if (!DisabledEntries.Contains(raw))
            DisabledEntries.Add(raw);
    }
}

public sealed class AppSettingsStore
{
    private readonly string _directory;

    public AppSettingsStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    public AppSettings Load()
    {
        var loaded = ConfigStore.Load<AppSettings>(_directory, AppSettings.FileName);
        // A missing file is a brand-new install, not a migration: it starts at
        // the current schema with the new-install defaults.
        return loaded is null ? new AppSettings() : loaded.Migrate();
    }

    public void Save(AppSettings settings) =>
        ConfigStore.Save(_directory, AppSettings.FileName, settings);
}
