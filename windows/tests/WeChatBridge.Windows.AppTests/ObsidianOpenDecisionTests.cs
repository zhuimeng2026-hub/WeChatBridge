using System.Text.Json.Nodes;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// End-to-end checks for the decision that decides how a note or a vault is
/// opened in Obsidian.
/// <para>
/// The bug these guard against is not a crash but a silent one: the URI
/// launched fine, the process started, and Obsidian answered with a "Vault
/// not found" dialog that the product never saw. The only way to catch that
/// is to know which vault Obsidian has registered and to never send it a path
/// it cannot resolve.
/// </para>
/// </summary>
public sealed class ObsidianOpenDecisionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatBridgeOpenTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly string? _previousOverride;

    public ObsidianOpenDecisionTests()
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

    private string NewFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteConfig(string json) => File.WriteAllText(_configPath, json);

    /// <summary>
    /// The vault the portable build ships, as Obsidian would see it: a folder
    /// with a .obsidian directory inside.
    /// </summary>
    private string NewPortableVault()
    {
        var path = NewFolder("Vault");
        Directory.CreateDirectory(Path.Combine(path, ".obsidian"));
        return path;
    }

    // --- the case from the bug report ---------------------------------------

    [Fact]
    public void A_vault_Obsidian_never_saw_is_not_resolvable()
    {
        // The portable vault exists and is a valid vault folder, but Obsidian
        // has never been told about it. Sending it a URI produces the
        // "Vault not found" dialog, so the product must notice this first.
        var vault = NewPortableVault();
        WriteConfig("{\"vaults\":{}}");

        Assert.False(ObsidianVaultRegistry.IsRegistered(vault));
        Assert.Null(ObsidianVaultRegistry.FindContaining(vault));
    }

    [Fact]
    public void A_note_in_an_unregistered_vault_is_not_resolvable_either()
    {
        var vault = NewPortableVault();
        var note = Path.Combine(vault, "31.微信会话", "2026.md");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        File.WriteAllText(note, "# chat");
        WriteConfig("{\"vaults\":{}}");

        Assert.Null(ObsidianVaultRegistry.FindContaining(note));
    }

    [Fact]
    public void Registering_the_portable_vault_makes_it_resolvable()
    {
        var vault = NewPortableVault();
        WriteConfig("{\"vaults\":{}}");

        Assert.True(ObsidianVaultRegistry.Register(vault, out var id, out _));

        var found = ObsidianVaultRegistry.FindContaining(vault);
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        // A note inside it resolves to the same vault.
        Assert.Equal(id, ObsidianVaultRegistry.FindContaining(
            Path.Combine(vault, "31.微信会话", "2026.md"))!.Id);
    }

    [Fact]
    public void Registering_keeps_the_vaults_the_user_already_had()
    {
        var mine = NewFolder("ob-vault");
        var portable = NewPortableVault();
        WriteConfig($"{{\"vaults\":{{\"e2caa29b723931f9\":{{\"path\":\"{mine.Replace("\\", "\\\\")}\",\"ts\":1700000000000}}}}}}");

        Assert.True(ObsidianVaultRegistry.Register(portable, out _, out _));

        var vaults = ObsidianVaultRegistry.List();
        Assert.Equal(2, vaults.Count);
        Assert.Contains(vaults, v => v.Path == mine);
        Assert.Contains(vaults, v => v.Path == portable);
    }

    [Fact]
    public void An_existing_backup_of_the_configuration_is_kept()
    {
        // This is somebody else's configuration file. A mistake here has to be
        // undoable, so the original is copied aside before it is rewritten.
        var portable = NewPortableVault();
        WriteConfig("{\"vaults\":{}}");

        Assert.True(ObsidianVaultRegistry.Register(portable, out _, out _));

        Assert.True(File.Exists(_configPath + ".bak"));
        Assert.Equal("{\"vaults\":{}}", File.ReadAllText(_configPath + ".bak"));
    }

    [Fact]
    public void The_written_configuration_is_valid_json_with_the_vault_in_it()
    {
        var portable = NewPortableVault();
        WriteConfig("{\"vaults\":{}}");

        Assert.True(ObsidianVaultRegistry.Register(portable, out var id, out _));

        var root = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        var vaults = root["vaults"]!.AsObject();
        Assert.True(vaults.ContainsKey(id!));
        Assert.Equal(portable, vaults[id!]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void A_vault_that_is_already_registered_is_left_alone()
    {
        // The user's own vault (D:\ob-vault in the bug report) must not be
        // re-registered, and its id must stay the same.
        var mine = NewFolder("ob-vault");
        WriteConfig($"{{\"vaults\":{{\"e2caa29b723931f9\":{{\"path\":\"{mine.Replace("\\", "\\\\")}\",\"ts\":1700000000000}}}}}}");

        Assert.True(ObsidianVaultRegistry.Register(mine, out var id, out _));

        Assert.Equal("e2caa29b723931f9", id);
        Assert.Single(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void Two_vaults_with_the_same_folder_name_do_not_confuse_the_lookup()
    {
        // The lookup is by path, not by name: a vault on D: and a vault with
        // the same folder name on E: are different vaults.
        var first = NewFolder("notes");
        var second = NewFolder("notes2");
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{{\"path\":\"{first.Replace("\\", "\\\\")}\",\"ts\":1700000000000}},"
            + $"\"bbbb\":{{\"path\":\"{second.Replace("\\", "\\\\")}\",\"ts\":1700000000000}}}}}}");

        Assert.Equal("aaaa", ObsidianVaultRegistry.FindContaining(first)!.Id);
        Assert.Equal("bbbb", ObsidianVaultRegistry.FindContaining(second)!.Id);
    }
}
