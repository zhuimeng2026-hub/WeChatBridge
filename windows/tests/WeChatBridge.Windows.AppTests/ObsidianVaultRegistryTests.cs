using System.Text.Json.Nodes;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// Tests for the Obsidian vault registry.
/// <para>
/// The registry is Obsidian's own configuration file, so every test points it
/// at a throwaway copy. A test that touched the real one would add vaults to
/// the developer's Obsidian and rewrite settings nobody asked it to.
/// </para>
/// </summary>
public sealed class ObsidianVaultRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatBridgeObsidianTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly string? _previousOverride;

    public ObsidianVaultRegistryTests()
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

    private void WriteConfig(string json) => File.WriteAllText(_configPath, json);

    /// <summary>
    /// One vault entry as Obsidian writes it: the path is a JSON string, so a
    /// backslash is escaped once. <paramref name="path"/> is a plain path
    /// (<c>D:\notes</c>), not an already-escaped one.
    /// </summary>
    private static string VaultJson(string id, string path, bool open = false)
    {
        var openPart = open ? ",\"open\":true" : "";
        return $"{{\"path\":\"{path.Replace("\\", "\\\\")}\",\"ts\":1700000000000{openPart}}}";
    }

    // --- listing ------------------------------------------------------------

    [Fact]
    public void List_returns_empty_when_config_does_not_exist()
    {
        Assert.Empty(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void List_returns_empty_for_corrupt_json()
    {
        WriteConfig("{ this is not json");
        Assert.Empty(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void List_returns_empty_when_vaults_is_missing()
    {
        WriteConfig("{\"cli\":true}");
        Assert.Empty(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void List_reads_every_registered_vault()
    {
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", @"D:\notes")},"
            + $"\"bbbb\":{VaultJson("bbbb", @"D:\work", true)}}}}}");

        var vaults = ObsidianVaultRegistry.List();

        Assert.Equal(2, vaults.Count);
        Assert.Contains(vaults, v => v.Id == "aaaa" && v.Path == @"D:\notes");
        Assert.Contains(vaults, v => v.Id == "bbbb" && v.Path == @"D:\work");
    }

    // --- path containment ---------------------------------------------------

    [Fact]
    public void FindContaining_matches_the_vault_root_itself()
    {
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", @"D:\notes")}}}}}" );

        var found = ObsidianVaultRegistry.FindContaining(@"D:\notes");

        Assert.NotNull(found);
        Assert.Equal("aaaa", found!.Id);
    }

    [Fact]
    public void FindContaining_matches_a_file_inside_the_vault()
    {
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", @"D:\notes")}}}}}" );

        var found = ObsidianVaultRegistry.FindContaining(@"D:\notes\chat\2026.md");

        Assert.NotNull(found);
        Assert.Equal("aaaa", found!.Id);
    }

    [Fact]
    public void FindContaining_ignores_a_sibling_with_a_shared_prefix()
    {
        // D:\notes-archive must not be treated as living inside D:\notes.
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", @"D:\notes")}}}}}" );

        Assert.Null(ObsidianVaultRegistry.FindContaining(@"D:\notes-archive\chat.md"));
    }

    [Fact]
    public void FindContaining_ignores_an_unrelated_path()
    {
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", @"D:\notes")}}}}}" );

        Assert.Null(ObsidianVaultRegistry.FindContaining(@"E:\elsewhere\chat.md"));
    }

    [Fact]
    public void FindContaining_returns_null_for_blank_input()
    {
        Assert.Null(ObsidianVaultRegistry.FindContaining(null));
        Assert.Null(ObsidianVaultRegistry.FindContaining("   "));
    }

    [Fact]
    public void FindContaining_prefers_the_deepest_matching_vault()
    {
        // Obsidian resolves open?path= to the most specific containing vault.
        WriteConfig($"{{\"vaults\":{{\"outer\":{VaultJson("outer", @"D:\vault")},"
            + $"\"inner\":{VaultJson("inner", @"D:\vault\sub")}}}}}" );

        var found = ObsidianVaultRegistry.FindContaining(@"D:\vault\sub\chat.md");

        Assert.NotNull(found);
        Assert.Equal("inner", found!.Id);
    }

    [Fact]
    public void FindContaining_ignores_a_trailing_separator_difference()
    {
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", @"D:\notes")}}}}}" );

        Assert.NotNull(ObsidianVaultRegistry.FindContaining(@"D:\notes\"));
    }

    // --- registration -------------------------------------------------------

    /// <summary>
    /// A real directory to register. Registration refuses a path that is not a
    /// directory, so the tests cannot invent one.
    /// </summary>
    private string NewFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Register_adds_the_vault_and_reports_its_id()
    {
        var folder = NewFolder("notes");

        Assert.True(ObsidianVaultRegistry.Register(folder, out var id, out var error));

        Assert.Null(error);
        Assert.NotNull(id);
        Assert.Equal(16, id!.Length);
        Assert.True(ObsidianVaultRegistry.IsRegistered(Path.Combine(folder, "chat.md")));
    }

    [Fact]
    public void Register_keeps_the_vaults_that_were_already_there()
    {
        var existing = NewFolder("notes");
        var added = NewFolder("work");
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", existing)}}},\"cli\":true}}" );

        Assert.True(ObsidianVaultRegistry.Register(added, out _, out _));

        var vaults = ObsidianVaultRegistry.List();
        Assert.Contains(vaults, v => v.Id == "aaaa" && v.Path == existing);
        Assert.Contains(vaults, v => v.Path == added);
    }

    [Fact]
    public void Register_keeps_unrelated_top_level_fields()
    {
        WriteConfig("{\"cli\":true,\"somethingElse\":42}");

        Assert.True(ObsidianVaultRegistry.Register(NewFolder("work"), out _, out _));

        var root = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.True(root["cli"]!.GetValue<bool>());
        Assert.Equal(42, root["somethingElse"]!.GetValue<int>());
    }

    [Fact]
    public void Register_is_idempotent_for_an_already_registered_vault()
    {
        var existing = NewFolder("notes");
        WriteConfig($"{{\"vaults\":{{\"aaaa\":{VaultJson("aaaa", existing)}}}}}" );

        Assert.True(ObsidianVaultRegistry.Register(existing, out var id, out _));

        Assert.Equal("aaaa", id);
        Assert.Single(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void Register_creates_the_config_when_it_is_missing()
    {
        Assert.True(ObsidianVaultRegistry.Register(NewFolder("work"), out _, out _));

        Assert.True(File.Exists(_configPath));
        Assert.Single(ObsidianVaultRegistry.List());
    }

    [Fact]
    public void Register_reports_failure_for_a_corrupt_config()
    {
        WriteConfig("{ not json at all");

        Assert.False(ObsidianVaultRegistry.Register(NewFolder("work"), out var id, out var error));

        Assert.Null(id);
        Assert.NotNull(error);
        // The unreadable file is left exactly as it was.
        Assert.Equal("{ not json at all", File.ReadAllText(_configPath));
    }

    [Fact]
    public void Register_reports_failure_for_a_blank_path()
    {
        Assert.False(ObsidianVaultRegistry.Register("   ", out var id, out var error));

        Assert.Null(id);
        Assert.NotNull(error);
    }

    [Fact]
    public void Register_reports_failure_for_a_directory_that_does_not_exist()
    {
        // A path that is not a directory cannot become a vault, and registering
        // one would leave Obsidian with a broken entry on the next start.
        var missing = Path.Combine(_root, "no-such-folder");

        Assert.False(ObsidianVaultRegistry.Register(missing, out var id, out var error));

        Assert.Null(id);
        Assert.NotNull(error);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Register_accepts_a_directory_that_exists()
    {
        var folder = NewFolder("vault");

        Assert.True(ObsidianVaultRegistry.Register(folder, out var id, out var error));

        Assert.Null(error);
        Assert.NotNull(id);
        Assert.True(ObsidianVaultRegistry.IsRegistered(folder));
    }

    [Fact]
    public void Register_leaves_no_temporary_file_behind()
    {
        Assert.True(ObsidianVaultRegistry.Register(NewFolder("work"), out _, out _));

        Assert.False(File.Exists(_configPath + ".tmp"));
    }

    [Fact]
    public void Register_writes_a_config_that_can_be_read_back()
    {
        var folder = NewFolder("work");

        Assert.True(ObsidianVaultRegistry.Register(folder, out _, out _));

        var root = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        var vault = root["vaults"]!.AsObject().First().Value!.AsObject();
        Assert.Equal(folder, vault["path"]!.GetValue<string>());
    }

    // --- vault initialisation ----------------------------------------------

    [Fact]
    public void EnsureVaultInitialized_creates_obsidian_dir_in_an_empty_folder()
    {
        var folder = NewFolder("empty");

        Assert.True(ObsidianVaultRegistry.EnsureVaultInitialized(folder, out var error));

        Assert.Null(error);
        Assert.True(Directory.Exists(Path.Combine(folder, ".obsidian")));
    }

    [Fact]
    public void EnsureVaultInitialized_is_idempotent_and_keeps_existing_config()
    {
        var folder = NewFolder("vault");
        var marker = Path.Combine(folder, ".obsidian", "app.json");
        Directory.CreateDirectory(Path.Combine(folder, ".obsidian"));
        File.WriteAllText(marker, "{\"custom\":true}");

        Assert.True(ObsidianVaultRegistry.EnsureVaultInitialized(folder, out var error));

        Assert.Null(error);
        Assert.True(File.Exists(marker));
        Assert.Equal("{\"custom\":true}", File.ReadAllText(marker));
    }

    [Fact]
    public void EnsureVaultInitialized_reports_failure_for_a_missing_directory()
    {
        var missing = Path.Combine(_root, "no-such-folder");

        Assert.False(ObsidianVaultRegistry.EnsureVaultInitialized(missing, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void EnsureVaultInitialized_reports_failure_for_a_blank_path()
    {
        Assert.False(ObsidianVaultRegistry.EnsureVaultInitialized("   ", out var error));

        Assert.NotNull(error);
    }
}
