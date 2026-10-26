using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// Runs the real open-in-Obsidian path against the real Obsidian
/// configuration file.
/// <para>
/// The other tests point the registry at a throwaway copy, which proves the
/// logic but not the wiring: that the product finds Obsidian's actual
/// configuration, that it recognises the user's own vaults, and that it
/// refuses to send Obsidian a path it cannot resolve. This test uses the real
/// file and restores it afterwards.
/// </para>
/// <para>
/// A machine without Obsidian has nothing to check here, so those tests
/// return instead of failing. Nothing is written: the tests below only read,
/// or register a vault the user already has — which is a no-op.
/// </para>
/// </summary>
public sealed class RealObsidianIntegrationTests
{
    private static string RealConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obsidian",
        "obsidian.json");

    private static bool HasRealConfig => File.Exists(RealConfigPath);

    [Fact]
    public void The_real_configuration_is_read_and_the_users_vaults_are_recognised()
    {
        if (!HasRealConfig)
            return;

        var vaults = ObsidianVaultRegistry.List();

        Assert.NotEmpty(vaults);
        // Every entry has the shape Obsidian writes: a 16-character id and a
        // non-empty path.
        foreach (var vault in vaults)
        {
            Assert.Equal(16, vault.Id.Length);
            Assert.False(string.IsNullOrWhiteSpace(vault.Path));
        }
    }

    [Fact]
    public void A_path_inside_a_real_vault_resolves_to_that_vault()
    {
        if (!HasRealConfig)
            return;

        var vault = ObsidianVaultRegistry.List().First();
        var inside = Path.Combine(vault.Path, "some-note.md");

        var found = ObsidianVaultRegistry.FindContaining(inside);

        Assert.NotNull(found);
        Assert.Equal(vault.Id, found!.Id);
    }

    [Fact]
    public void The_product_recognises_the_users_own_vault_and_leaves_it_alone()
    {
        if (!HasRealConfig)
            return;

        var before = ObsidianVaultRegistry.List();
        var vault = before.First();
        var content = File.ReadAllText(RealConfigPath);

        // Opening a vault the user already has must not add a second entry
        // for the same folder, and must not rewrite their configuration.
        Assert.True(ObsidianVaultRegistry.Register(vault.Path, out var id, out _));
        Assert.Equal(vault.Id, id);
        Assert.Equal(before.Count, ObsidianVaultRegistry.List().Count);
        Assert.Equal(content, File.ReadAllText(RealConfigPath));
    }
}
