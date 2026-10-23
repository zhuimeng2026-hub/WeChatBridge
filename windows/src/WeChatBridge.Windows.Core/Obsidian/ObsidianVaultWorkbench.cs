using System.Text;
using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>Read-only vault browsing and create-only analysis note saving for the workbench.</summary>
public sealed class ObsidianVaultWorkbench
{
    private readonly string _vaultRoot;
    private readonly string _analysisFolder;

    public ObsidianVaultWorkbench(string vaultRoot, string analysisFolder = "分析")
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) throw new ArgumentException("Vault path is required.", nameof(vaultRoot));
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _analysisFolder = SafeRelativeFolder(analysisFolder);
    }

    public string VaultRoot => _vaultRoot;

    /// <summary>Lists Markdown files under the selected vault, excluding links and hidden folders.</summary>
    public IReadOnlyList<string> ListMarkdownFiles()
    {
        if (!Directory.Exists(_vaultRoot)) return [];
        if ((new DirectoryInfo(_vaultRoot).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked vault folders are not supported.");
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_vaultRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0 && !info.Name.StartsWith(".", StringComparison.Ordinal))
                    pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                    result.Add(Path.GetRelativePath(_vaultRoot, file).Replace('\\', '/'));
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Reads a Markdown note by vault-relative path, with a 2 MiB bound.</summary>
    public string ReadMarkdown(string relativePath)
    {
        var path = ResolveContained(relativePath);
        if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Markdown notes can be opened in the workbench.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The selected note no longer exists.", path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked files cannot be opened from the workbench.");
        if (info.Length > 2 * 1024 * 1024) throw new InvalidOperationException("The selected note is larger than 2 MiB.");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>
    /// Lists adjacent files and vault-contained wiki-link attachments. Existing
    /// archive notes put their originals in 附件/, rather than beside the note.
    /// Attachment bytes are never included in a model request by this reader.
    /// </summary>
    public IReadOnlyList<ObsidianAttachment> ListAttachments(string relativeMarkdownPath)
    {
        var markdown = ReadMarkdown(relativeMarkdownPath);
        var notePath = ResolveContained(relativeMarkdownPath);
        var folder = Path.GetDirectoryName(notePath)!;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder)) candidates.Add(path);
        foreach (Match match in Regex.Matches(markdown, @"!?\[\[(?<path>[^\]#|]+)(?:[#|][^\]]*)?\]\]"))
        {
            var link = match.Groups["path"].Value.Trim();
            if (Path.IsPathRooted(link) || link.Contains(':')) continue;
            try
            {
                // Obsidian resolves the exporter’s 附件/... links beside its note.
                var local = Path.GetRelativePath(_vaultRoot, Path.GetFullPath(Path.Combine(folder, link)));
                candidates.Add(ResolveContained(local));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
            {
                // An optional broken/outside attachment must not hide the note.
            }
        }
        var attachments = new List<ObsidianAttachment>();
        foreach (var candidate in candidates)
        {
            try
            {
                var relative = Path.GetRelativePath(_vaultRoot, candidate);
                var info = new FileInfo(ResolveContained(relative));
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0
                    || info.Name.StartsWith(".", StringComparison.Ordinal)
                    || string.Equals(info.Extension, ".md", StringComparison.OrdinalIgnoreCase)) continue;
                attachments.Add(new ObsidianAttachment(relative.Replace('\\', '/'), info.Name, info.Length));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Metadata is best-effort; the original archive remains untouched.
            }
        }
        return attachments.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Resolves an existing contained attachment for explicit folder reveal.</summary>
    public string GetAttachmentPath(string relativePath)
    {
        var path = ResolveContained(relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The attachment no longer exists.", path);
        return path;
    }

    /// <summary>Saves a new Markdown note beneath the vault analysis folder; existing files are never overwritten.</summary>
    public string SaveNewAnalysis(string title, string markdown)
    {
        var safeTitle = SafeFileName(title);
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("Note content is required.", nameof(markdown));
        var folder = ResolveContained(_analysisFolder);
        Directory.CreateDirectory(folder);
        // A folder can be replaced with a junction after initial path resolution.
        // Recheck after creation to reject any junction that is already present.
        _ = ResolveContained(_analysisFolder);
        var basePath = Path.Combine(folder, safeTitle + ".md");
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = attempt == 0 ? basePath : Path.Combine(folder, $"{safeTitle} {attempt + 1}.md");
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
                return Path.GetRelativePath(_vaultRoot, candidate).Replace('\\', '/');
            }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        throw new IOException("Could not choose a new analysis note filename.");
    }

    private string ResolveContained(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("A vault-relative path is required.", nameof(relativePath));
        var path = Path.GetFullPath(Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _vaultRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The path must stay inside the selected vault.");
        RejectReparseComponents(path);
        return path;
    }

    private void RejectReparseComponents(string path)
    {
        var rootInfo = new DirectoryInfo(_vaultRoot);
        if (rootInfo.Exists && (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked vault folders are not supported.");

        var relative = Path.GetRelativePath(_vaultRoot, path);
        var current = _vaultRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked files and folders cannot be opened from the workbench.");
        }
    }

    private static string SafeRelativeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Analysis folder is required.", nameof(folder));
        var normalizedInput = folder.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedInput))
            throw new ArgumentException("Analysis folder must be a safe vault-relative folder.", nameof(folder));
        var normalized = normalizedInput.Trim('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Analysis folder must be a safe vault-relative folder.", nameof(folder));
        return normalized;
    }

    private static string SafeFileName(string title)
    {
        var value = string.IsNullOrWhiteSpace(title) ? DefaultDraftTitle() : title.Trim().Replace('\\', '/').Split('/').Last();
        foreach (var invalid in Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*")) value = value.Replace(invalid, '-');
        value = value.Trim().Trim('.');
        if (value.Length == 0) value = DefaultDraftTitle();
        return value.Length > 100 ? value[..100] : value;
    }

    /// <summary>
    /// The file name a note falls back to when it is saved without a title.
    /// Trimmed: the English translation of「分析草稿」carries a leading space —
    /// the Chinese suffix needs none, English needs one so it does not run into
    /// the material's file name — and a file name may not begin with a space.
    /// </summary>
    private static string DefaultDraftTitle() => L10n.Text("分析草稿").Trim();
}

public sealed record ObsidianAttachment(string RelativePath, string Name, long ByteCount);
