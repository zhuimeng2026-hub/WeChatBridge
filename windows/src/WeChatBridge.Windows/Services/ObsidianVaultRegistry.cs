using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// One vault as Obsidian's own configuration records it.
/// </summary>
/// <param name="Id">
/// Obsidian's 16-character vault id. The URI scheme accepts either this or
/// the vault's folder name, but the id is unambiguous: two vaults may share a
/// folder name on different drives.
/// </param>
/// <param name="Path">Absolute path of the vault folder.</param>
public sealed record ObsidianVaultEntry(string Id, string Path);

/// <summary>
/// Reads and updates Obsidian's own vault registry — the <c>obsidian.json</c>
/// that lives beside the application data, not the <c>obsidian.json</c> this
/// product writes under <c>%LOCALAPPDATA%\WeChatBridge\Config</c> (that one is
/// a projection of the destination, this one is Obsidian's truth).
/// <para>
/// This exists because <c>obsidian://open?path=…</c> only resolves a path that
/// already sits inside a vault Obsidian knows about. Point it at a folder
/// Obsidian has never seen and Obsidian answers with a "Vault not found"
/// dialog instead of opening anything — the URI succeeds, the launch succeeds,
/// and the failure is invisible to the caller. Checking the registry first
/// turns that silent failure into something the product can act on.
/// </para>
/// <para>
/// Registering a vault means editing Obsidian's configuration file. Obsidian
/// reads that file at startup and rewrites it on exit, so a vault registered
/// while Obsidian is running is not recognised until the next start. Callers
/// have to know that; <see cref="Register"/> reports it rather than pretending
/// the registration took effect.
/// </para>
/// </summary>
internal static class ObsidianVaultRegistry
{
    /// <summary>Windows path of Obsidian's configuration file.</summary>
    private static string ConfigPath => ConfigPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obsidian",
        "obsidian.json");

    /// <summary>
    /// Overrides the configuration path. Tests point this at a temporary file
    /// so they never touch the real Obsidian settings; production leaves it
    /// null and the real path is used.
    /// </summary>
    internal static string? ConfigPathOverride { get; set; }

    /// <summary>
    /// Every vault Obsidian currently knows about. An unreadable or corrupt
    /// configuration reads as "no vaults" — never an exception, because a
    /// broken file must not stop the product from delivering notes.
    /// </summary>
    public static IReadOnlyList<ObsidianVaultEntry> List()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
            return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("vaults", out var vaults)
                || vaults.ValueKind != JsonValueKind.Object)
                return [];
            var entries = new List<ObsidianVaultEntry>();
            foreach (var vault in vaults.EnumerateObject())
            {
                if (!vault.Value.TryGetProperty("path", out var vaultPath)
                    || vaultPath.ValueKind != JsonValueKind.String)
                    continue;
                var text = vaultPath.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                entries.Add(new ObsidianVaultEntry(vault.Name, text));
            }
            return entries;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The vault that contains <paramref name="path"/>, or null when no
    /// registered vault contains it.
    /// <para>
    /// The comparison is on path boundaries, not on string prefixes: a folder
    /// named <c>D:\notes</c> does not contain <c>D:\notes-archive</c>. When
    /// several registered vaults could contain the path, the deepest one wins,
    /// matching Obsidian's own "most specific vault" rule for
    /// <c>open?path=</c>.
    /// </para>
    /// </summary>
    public static ObsidianVaultEntry? FindContaining(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var target = Normalize(path);
        ObsidianVaultEntry? best = null;
        foreach (var vault in List())
        {
            var root = Normalize(vault.Path);
            if (root.Length == 0)
                continue;
            if (!target.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is null || root.Length > Normalize(best.Path).Length)
                best = vault;
        }
        return best;
    }

    /// <summary>True when Obsidian already knows a vault containing the path.</summary>
    public static bool IsRegistered(string? path) => FindContaining(path) is not null;

    /// <summary>
    /// True when Obsidian is running.
    /// <para>
    /// This matters for registration: Obsidian reads its configuration at
    /// startup and rewrites it on exit, so a vault added while it runs is not
    /// in its in-memory list and stays invisible until the next start. A
    /// caller that has just registered a vault needs to know whether to try
    /// the URI now or to tell the user to restart Obsidian.
    /// </para>
    /// </summary>
    public static bool IsRunning()
    {
        System.Diagnostics.Process[] processes;
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName("Obsidian");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// Adds the vault to Obsidian's configuration.
    /// </summary>
    /// <param name="vaultPath">The vault folder to register.</param>
    /// <param name="registered">
    /// The id of the registered vault — an existing one when the vault was
    /// already known, otherwise the freshly created one.
    /// </param>
    /// <param name="error">Why the registration failed, when it did.</param>
    /// <returns>
    /// True when the configuration file now lists the vault. Note this is not
    /// the same as Obsidian recognising it: Obsidian reads the file at
    /// startup, so a vault added while Obsidian runs is only picked up after
    /// the next start.
    /// </returns>
    public static bool Register(string vaultPath, out string? registered, out string? error)
    {
        registered = null;
        error = null;

        var existing = FindContaining(vaultPath);
        if (existing is not null)
        {
            registered = existing.Id;
            return true;
        }

        var normalized = Normalize(vaultPath);
        if (normalized.Length == 0)
        {
            error = "知识库路径为空。";
            return false;
        }

        // Obsidian opens whatever folder it is told to open, but a path that
        // is not a directory cannot become a vault, and registering one would
        // leave Obsidian with a broken entry on the next start.
        if (!Directory.Exists(normalized))
        {
            error = $"知识库目录不存在：{normalized}";
            return false;
        }

        var path = ConfigPath;
        JsonObject root;
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                root = document.RootElement.ValueKind == JsonValueKind.Object
                    ? (JsonObject)JsonNode.Parse(document.RootElement.GetRawText())!
                    : new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }
        }
        catch (Exception read) when (read is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            error = $"无法读取 Obsidian 配置：{read.Message}";
            return false;
        }

        if (root["vaults"] is not JsonObject vaults)
        {
            vaults = new JsonObject();
            root["vaults"] = vaults;
        }

        // Obsidian assigns a random 16-character id per folder. Reusing an
        // existing id would point two folders at one vault, so a fresh random
        // one is generated and checked for collisions.
        var id = NewId(vaults);
        vaults[id] = new JsonObject
        {
            ["path"] = normalized,
            ["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            // Keep the original next to the new one: this is somebody else's
            // configuration file, and a mistake here should be undoable.
            if (File.Exists(path))
            {
                try
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch (Exception backup) when (backup is IOException or UnauthorizedAccessException)
                {
                    // A missing backup must not stop the registration.
                }
            }
            // Write to a sibling file and move it into place: a crash halfway
            // through must not leave Obsidian with a half-written registry.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            error = $"无法写入 Obsidian 配置：{write.Message}";
            return false;
        }

        registered = id;
        return true;
    }

    /// <summary>
    /// Creates the vault folder when it does not exist yet.
    /// <para>
    /// The user typed this path on purpose: it is where their notes should
    /// live. A folder that is not there yet is a folder to create, not a
    /// broken setting — so the vault is made here instead of being refused.
    /// A drive that is not connected still fails, and the operating
    /// system's own reason is passed back.
    /// </para>
    /// </summary>
    /// <param name="vaultPath">The vault folder to create.</param>
    /// <param name="detail">Why it failed, when it did.</param>
    /// <returns>True when the folder exists afterwards.</returns>
    public static bool EnsureVaultCreated(string vaultPath, out string? detail)
    {
        detail = null;
        var normalized = Normalize(vaultPath);
        if (Directory.Exists(normalized))
            return true;
        try
        {
            // Creates every missing level, so a path like D:\notes\vault
            // works even when D:\notes does not exist either.
            Directory.CreateDirectory(normalized);
            return true;
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            detail = write.Message;
            return false;
        }
    }

    /// <summary>
    /// Turns a plain folder into a usable Obsidian vault.
    /// <para>
    /// Obsidian treats any folder that contains a <c>.obsidian</c> directory as
    /// a vault; an empty folder the user just picked in Settings is not one
    /// yet, and merely adding it to <c>obsidian.json</c> (see <see cref="Register"/>)
    /// is not enough — Obsidian only reads that file at startup, and a folder
    /// with no <c>.obsidian</c> of its own is not something it will open cleanly.
    /// Creating <c>.obsidian</c> makes the empty pick a real vault so delivery
    /// and <c>obsidian://open</c> resolve against it.
    /// </para>
    /// <para>
    /// Idempotent: a folder that already has <c>.obsidian</c> is left untouched,
    /// and a folder the user pointed at a pre-existing vault keeps whatever
    /// Obsidian already wrote there.
    /// </para>
    /// </summary>
    /// <param name="vaultPath">The vault folder to initialise.</param>
    /// <param name="error">Why the initialisation failed, when it did.</param>
    /// <returns>True when the folder now contains a <c>.obsidian</c> directory.</returns>
    public static bool EnsureVaultInitialized(string vaultPath, out string? error)
    {
        error = null;
        var normalized = Normalize(vaultPath);
        if (normalized.Length == 0)
        {
            error = "知识库路径为空。";
            return false;
        }
        if (!Directory.Exists(normalized))
        {
            error = $"知识库目录不存在：{normalized}";
            return false;
        }
        var obsidianDir = Path.Combine(normalized, ".obsidian");
        try
        {
            if (!Directory.Exists(obsidianDir))
                Directory.CreateDirectory(obsidianDir);
            return true;
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            error = $"无法初始化知识库目录：{write.Message}";
            return false;
        }
    }

    /// <summary>
    /// A 16-character hex id that is not already in use. Obsidian's own ids
    /// look like this and nothing else depends on their value.
    /// </summary>
    private static string NewId(JsonObject vaults)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (!vaults.ContainsKey(id))
                return id;
        }
        // 64 collisions of a 64-bit id cannot happen in practice; falling back
        // to a timestamp keeps the method total rather than throwing.
        return DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString("x16");
    }

    /// <summary>
    /// A comparable form of a path: no trailing separator, and no case
    /// difference on Windows.
    /// </summary>
    private static string Normalize(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
