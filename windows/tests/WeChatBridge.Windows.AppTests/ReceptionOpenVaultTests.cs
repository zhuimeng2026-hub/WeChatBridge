using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// End-to-end tests for "打开知识库".
/// <para>
/// The bug these guard against produced an Obsidian dialog the product never
/// saw: the URI was sent for a vault Obsidian had never been told about, and
/// the only symptom was a "Vault not found" window on the user's screen. The
/// product has to notice the situation itself, and it has to say what it did
/// in the log — the log is the only record of an outcome the user cannot
/// otherwise report.
/// </para>
/// </summary>
public sealed class ReceptionOpenVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatBridgeReceptionTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly string? _previousOverride;

    public ReceptionOpenVaultTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "obsidian.json");
        _previousOverride = ObsidianVaultRegistry.ConfigPathOverride;
        ObsidianVaultRegistry.ConfigPathOverride = _configPath;
    }

    public void Dispose()
    {
        ObsidianVaultRegistry.ConfigPathOverride = _previousOverride;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A vault folder with the .obsidian marker Obsidian requires.</summary>
    private string NewVault()
    {
        var path = Path.Combine(_root, "Vault");
        Directory.CreateDirectory(Path.Combine(path, ".obsidian"));
        return path;
    }

    private AppSettingsStore NewSettingsStore(string vaultPath)
    {
        var directory = Path.Combine(_root, "Config");
        Directory.CreateDirectory(directory);
        var store = new AppSettingsStore(directory);
        var settings = store.Load();
        settings.ObsidianVaultPath = vaultPath;
        settings.ObsidianSubfolder = "微信聊天";
        store.Save(settings);
        return store;
    }

    private static string ReadLog(InboxPaths paths)
    {
        var log = Path.Combine(paths.Logs, "windows.log");
        return File.Exists(log) ? File.ReadAllText(log) : "";
    }

    [Fact]
    public void Opening_an_unregistered_vault_records_what_happened()
    {
        // The portable vault exists, but Obsidian has never seen it. The
        // product must not silently hand it to Obsidian and it must leave a
        // record of the attempt.
        var vault = NewVault();
        File.WriteAllText(_configPath, "{\"vaults\":{}}");

        var paths = new InboxPaths(Path.Combine(_root, "Inbox"));
        using var service = new ReceptionService(NewSettingsStore(vault), paths);
        var notices = new List<string>();
        service.NotificationRequested += m => notices.Add(m);

        service.OpenVault();

        var log = ReadLog(paths);
        Assert.Contains("打开知识库", log);
        // The vault was not known, so the log has to say so rather than
        // reporting a plain success.
        Assert.Contains("注册", log);
    }

    [Fact]
    public void Opening_an_unregistered_vault_does_not_leave_a_registration_behind_when_it_fails()
    {
        // A vault folder that does not exist cannot be registered, and the
        // attempt must not write a broken entry into Obsidian's configuration.
        var missing = Path.Combine(_root, "no-such-vault");
        File.WriteAllText(_configPath, "{\"vaults\":{}}");

        var paths = new InboxPaths(Path.Combine(_root, "Inbox"));
        using var service = new ReceptionService(NewSettingsStore(missing), paths);
        var notices = new List<string>();
        service.NotificationRequested += m => notices.Add(m);

        service.OpenVault();

        var log = ReadLog(paths);
        Assert.Contains("打开知识库失败", log);
        // No entry was added: the vaults dictionary is still empty.
        Assert.Empty(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void Opening_an_unregistered_vault_registers_it_for_next_time()
    {
        var vault = NewVault();
        File.WriteAllText(_configPath, "{\"vaults\":{}}");

        var paths = new InboxPaths(Path.Combine(_root, "Inbox"));
        using var service = new ReceptionService(NewSettingsStore(vault), paths);
        service.NotificationRequested += _ => { };

        service.OpenVault();

        // Whatever the outcome of this attempt was, the vault is known to
        // Obsidian afterwards, so the next attempt resolves.
        Assert.True(ObsidianVaultRegistry.IsRegistered(vault));
    }

    [Fact]
    public void Opening_a_registered_vault_leaves_the_registry_alone()
    {
        var vault = NewVault();
        var id = "e2caa29b723931f9";
        File.WriteAllText(_configPath,
            "{\"vaults\":{\"" + id + "\":{\"path\":\"" + vault.Replace("\\", "\\\\") + "\",\"ts\":1700000000000}}}");

        var paths = new InboxPaths(Path.Combine(_root, "Inbox"));
        using var service = new ReceptionService(NewSettingsStore(vault), paths);
        service.NotificationRequested += _ => { };

        service.OpenVault();

        // The entry the user already had is untouched — same id, same file.
        var vaults = ObsidianVaultRegistry.List();
        Assert.Single(vaults);
        Assert.Equal(id, vaults[0].Id);
    }

    [Fact]
    public void Opening_a_registered_vault_records_the_attempt()
    {
        var vault = NewVault();
        File.WriteAllText(_configPath,
            "{\"vaults\":{\"e2caa29b723931f9\":{\"path\":\"" + vault.Replace("\\", "\\\\") + "\",\"ts\":1700000000000}}}");

        var paths = new InboxPaths(Path.Combine(_root, "Inbox"));
        using var service = new ReceptionService(NewSettingsStore(vault), paths);
        service.NotificationRequested += _ => { };

        service.OpenVault();

        var log = ReadLog(paths);
        Assert.Contains("打开知识库", log);
    }

    [Fact]
    public void A_missing_vault_is_reported_in_the_log()
    {
        var missing = Path.Combine(_root, "gone");
        var paths = new InboxPaths(Path.Combine(_root, "Inbox"));
        using var service = new ReceptionService(NewSettingsStore(missing), paths);
        var notices = new List<string>();
        service.NotificationRequested += m => notices.Add(m);

        service.OpenVault();

        Assert.Contains("打开知识库失败", ReadLog(paths));
        Assert.NotEmpty(notices);
    }
}
