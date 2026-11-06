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
    /// <para>
    /// One exception to "untouched": a vault whose <c>app.json</c> never
    /// mentioned <c>showUnsupportedFiles</c> gets that key seeded to true, so
    /// the delivered <c>docx</c>/<c>xlsx</c> attachments are not hidden from
    /// Obsidian's file explorer. See
    /// <see cref="EnsureUnsupportedFilesVisible"/> for why that matters. The
    /// technical leftovers that switch also un-hides are filtered again by a
    /// CSS snippet — see <see cref="EnsureTechnicalFilesHidden"/>.
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
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            error = $"无法初始化知识库目录：{write.Message}";
            return false;
        }

        // Best effort: a vault Obsidian cannot list .docx inside still works as
        // a vault, so a failed seed must not fail the initialisation. Reasons
        // are the ones the product already treats as non-fatal (read-only
        // folder, file locked by a running Obsidian).
        SeedUnsupportedFilesVisible(obsidianDir);
        SeedTechnicalFilesHidden(obsidianDir);
        return true;
    }

    /// <summary>Obsidian's per-vault settings file, inside <c>.obsidian</c>.</summary>
    private const string AppConfigFileName = "app.json";

    /// <summary>
    /// Obsidian's switch for showing file types it cannot open itself. The
    /// settings UI calls it "Detect all file extensions" /
    /// 「显示所有类型文件」.
    /// </summary>
    private const string ShowUnsupportedFilesKey = "showUnsupportedFiles";

    /// <summary>
    /// Turns on "show files Obsidian cannot open" for this vault, so delivered
    /// <c>docx</c>/<c>xlsx</c> attachments are visible instead of turning their
    /// category folders into empty ones.
    /// <para>
    /// The reason this has to be done by the product: Obsidian's file explorer
    /// and its link index only carry the extensions Obsidian opens natively
    /// (markdown, PDF, images, audio, video). Every other extension is filtered
    /// out of the tree and out of <c>[[…]]</c> resolution — the folder exists,
    /// the file is on disk, and the user sees an empty folder with a grey,
    /// un-clickable link in the note. Word and spreadsheet attachments are
    /// delivered into exactly those filtered categories, so every fresh vault
    /// reproduced it.
    /// </para>
    /// <para>
    /// Merge-only and idempotent: everything else in <c>app.json</c> is kept
    /// verbatim, a key that is already present is respected (including an
    /// explicit <c>false</c> — the user may have chosen to hide the archives),
    /// and an unreadable or non-object file is left alone rather than
    /// overwritten with our own idea of it.
    /// </para>
    /// <para>
    /// Obsidian reads this file at startup, so the change becomes visible on
    /// the next start when Obsidian is already running.
    /// </para>
    /// </summary>
    /// <returns>True when the vault now requests unsupported files visible.</returns>
    public static bool EnsureUnsupportedFilesVisible(string vaultPath, out string? error)
    {
        error = null;
        var normalized = Normalize(vaultPath);
        if (normalized.Length == 0)
        {
            error = "知识库路径为空。";
            return false;
        }
        var obsidianDir = Path.Combine(normalized, ".obsidian");
        if (!Directory.Exists(obsidianDir))
        {
            error = $"知识库尚未初始化：{normalized}";
            return false;
        }
        if (!SeedUnsupportedFilesVisible(obsidianDir))
        {
            error = Path.Combine(obsidianDir, AppConfigFileName);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The merge itself. <paramref name="obsidianDir"/> is the vault's
    /// <c>.obsidian</c> folder, which the caller has already confirmed exists.
    /// </summary>
    private static bool SeedUnsupportedFilesVisible(string obsidianDir)
    {
        var path = Path.Combine(obsidianDir, AppConfigFileName);
        JsonObject root;
        try
        {
            if (!TryLoadJsonObject(path, out root))
                return true;
        }
        catch (Exception read) when (read is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            return false;
        }

        // Already answered — either by us on an earlier run or by the user in
        // Obsidian's settings. Nothing to add.
        if (root.ContainsKey(ShowUnsupportedFilesKey))
            return true;

        root[ShowUnsupportedFilesKey] = true;
        return WriteJsonAtomically(path, root);
    }

    /// <summary>Folder Obsidian loads CSS snippets from, inside <c>.obsidian</c>.</summary>
    private const string SnippetsFolderName = "snippets";

    /// <summary>
    /// Name of the snippet that keeps technical junk out of the file explorer.
    /// Extension-less on purpose: Obsidian registers snippets by file stem.
    /// </summary>
    private const string TechnicalFilesSnippet = "hide-technical-files";

    /// <summary>
    /// The snippet itself. Hides, in the file explorer only, the extensions a
    /// lawyer never works with — raw archives, installers, binaries, scripts —
    /// plus the <c>原始归档</c> folder that holds the WeChat export.
    /// <para>
    /// <c>data-path</c> suffix matching is case-insensitive (<c>i</c> flag) so
    /// <c>.ZIP</c> from a Windows machine is covered as well. Nothing is hidden
    /// once the snippet is switched off, and no file moves.
    /// </para>
    /// </summary>
    private const string TechnicalFilesCss = """
        /*
         * 律档通生成：文件树只显示办案会用到的文件类型。
         * 原始压缩包、安装包、可执行文件、脚本留在磁盘上，只是不在 Obsidian 里出现；
         * 笔记里的链接仍然可以点击。
         * 本文件由程序维护，重新保存知识库设置时会被覆盖。
         */

        .nav-folder-title[data-path$="/原始归档"],
        .nav-file-title[data-path$=".zip" i],
        .nav-file-title[data-path$=".rar" i],
        .nav-file-title[data-path$=".7z" i],
        .nav-file-title[data-path$=".tar" i],
        .nav-file-title[data-path$=".gz" i],
        .nav-file-title[data-path$=".tgz" i],
        .nav-file-title[data-path$=".bz2" i],
        .nav-file-title[data-path$=".xz" i],
        .nav-file-title[data-path$=".msi" i],
        .nav-file-title[data-path$=".msix" i],
        .nav-file-title[data-path$=".appx" i],
        .nav-file-title[data-path$=".exe" i],
        .nav-file-title[data-path$=".dll" i],
        .nav-file-title[data-path$=".sys" i],
        .nav-file-title[data-path$=".cab" i],
        .nav-file-title[data-path$=".iso" i],
        .nav-file-title[data-path$=".bat" i],
        .nav-file-title[data-path$=".cmd" i],
        .nav-file-title[data-path$=".ps1" i],
        .nav-file-title[data-path$=".vbs" i],
        .nav-file-title[data-path$=".reg" i],
        .nav-file-title[data-path$=".tmp" i],
        .nav-file-title[data-path$=".bak" i],
        .nav-file-title[data-path$=".lnk" i] {
          display: none;
        }

        """;

    /// <summary>
    /// Keeps the attachments a lawyer has no use for out of Obsidian's file
    /// explorer, without moving anything on disk.
    /// <para>
    /// Two settings have to work together and neither alone is enough.
    /// <see cref="EnsureUnsupportedFilesVisible"/> is what makes the delivered
    /// <c>docx</c>/<c>xlsx</c> appear at all, but it is all-or-nothing: it also
    /// un-hides the raw ZIP in <c>原始归档</c> and anything technical that
    /// arrived with a client's files. Obsidian's own "Excluded files" setting
    /// looks like the answer and is not — it only affects search, graph and
    /// backlinks, never the file explorer. A CSS snippet is the one native
    /// mechanism that removes an entry from the tree, and it leaves the path
    /// untouched, so the note's <c>[[原始归档/…zip]]</c> link keeps working.
    /// </para>
    /// <para>
    /// Product-managed and idempotent: the snippet is rewritten whenever its
    /// text changes, so a new extension reaches existing vaults on the next
    /// settings save. Everything else in <c>appearance.json</c> — including
    /// snippets the user enabled themselves — is preserved. Both files are
    /// read by Obsidian at startup, so a running Obsidian shows the result
    /// after a restart.
    /// </para>
    /// </summary>
    /// <returns>True when the snippet exists and is enabled for the vault.</returns>
    public static bool EnsureTechnicalFilesHidden(string vaultPath, out string? error)
    {
        error = null;
        var normalized = Normalize(vaultPath);
        if (normalized.Length == 0)
        {
            error = "知识库路径为空。";
            return false;
        }
        var obsidianDir = Path.Combine(normalized, ".obsidian");
        if (!Directory.Exists(obsidianDir))
        {
            error = $"知识库尚未初始化：{normalized}";
            return false;
        }
        if (!SeedTechnicalFilesHidden(obsidianDir))
        {
            error = "无法写入知识库的显示设置。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the snippet and enables it. <paramref name="obsidianDir"/> is the
    /// vault's <c>.obsidian</c> folder, which the caller has already confirmed
    /// exists.
    /// </summary>
    private static bool SeedTechnicalFilesHidden(string obsidianDir)
    {
        try
        {
            var snippets = Path.Combine(obsidianDir, SnippetsFolderName);
            Directory.CreateDirectory(snippets);
            var snippetPath = Path.Combine(snippets, TechnicalFilesSnippet + ".css");
            var existing = File.Exists(snippetPath) ? File.ReadAllText(snippetPath) : null;
            if (!string.Equals(existing, TechnicalFilesCss, StringComparison.Ordinal))
                File.WriteAllText(snippetPath, TechnicalFilesCss);
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }

        var appearancePath = Path.Combine(obsidianDir, "appearance.json");
        JsonObject root;
        bool changed;
        try
        {
            if (!TryLoadJsonObject(appearancePath, out root))
                return true;
            var enabled = root["enabledCssSnippets"] as JsonArray;
            changed = enabled is null;
            if (enabled is null)
            {
                enabled = new JsonArray();
                root["enabledCssSnippets"] = enabled;
            }
            var already = enabled.Any(node =>
                node is JsonValue value
                && value.TryGetValue<string>(out var name)
                && string.Equals(name, TechnicalFilesSnippet, StringComparison.Ordinal));
            if (!already)
            {
                enabled.Add(TechnicalFilesSnippet);
                changed = true;
            }
        }
        catch (Exception read) when (read is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            return false;
        }

        // Untouched when it already lists the snippet: this file belongs to
        // Obsidian, and a rewrite every save is noise it has to re-read.
        return !changed || WriteJsonAtomically(appearancePath, root);
    }

    /// <summary>
    /// Reads a JSON settings file into an object.
    /// <para>
    /// A missing or empty file yields a fresh object (the caller is about to
    /// create it). Returns false when the file exists but is not a JSON object:
    /// that is somebody's hand-made file, and merging into it would overwrite
    /// whatever shape they chose.
    /// </para>
    /// </summary>
    private static bool TryLoadJsonObject(string path, out JsonObject root)
    {
        root = new JsonObject();
        if (!File.Exists(path))
            return true;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return true;
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        root = (JsonObject)JsonNode.Parse(document.RootElement.GetRawText())!;
        return true;
    }

    /// <summary>
    /// Writes the object beside the original and moves it into place, so a
    /// crash cannot leave Obsidian with a half-written settings file.
    /// </summary>
    private static bool WriteJsonAtomically(string path, JsonObject root)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
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
