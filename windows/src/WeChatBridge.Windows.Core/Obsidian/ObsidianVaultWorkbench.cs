using System.Text;
using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>Read-only vault browsing and create-only analysis note saving for the workbench.</summary>
public class ObsidianVaultWorkbench
{
    private readonly string _vaultRoot;
    private readonly string _analysisFolder;

    public ObsidianVaultWorkbench(string vaultRoot, string analysisFolder = "分析")
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) throw new ArgumentException(L10n.Text("未选择知识库"), nameof(vaultRoot));
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _analysisFolder = SafeRelativeFolder(analysisFolder);
    }

    public string VaultRoot => _vaultRoot;

    /// <summary>
    /// The directory enumeration seam. Overridable so the test suite can
    /// simulate an unreadable folder — an ACL-denied directory is awkward to
    /// create from a test that runs as the owner of the vault.
    /// </summary>
    protected virtual IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    /// <summary>The Markdown files of one directory. See <see cref="EnumerateDirectories"/>.</summary>
    protected virtual IEnumerable<string> EnumerateMarkdownFiles(string path) => Directory.EnumerateFiles(path, "*.md");

    /// <summary>Every file of one directory. See <see cref="EnumerateDirectories"/>.</summary>
    protected virtual IEnumerable<string> EnumerateAllFiles(string path) => Directory.EnumerateFiles(path);

    /// <summary>
    /// Writes the note body into a freshly created file. Overridable so the
    /// test suite can simulate a write that fails midway (a full disk, an
    /// antivirus lock) — neither is easy to arrange on a real disk.
    /// </summary>
    protected virtual void WriteNote(FileStream stream, string markdown)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// Lists Markdown files under the selected vault, excluding links and hidden folders.
    /// <para>
    /// One unreadable folder does not take the listing down with it: a vault
    /// with thousands of readable notes and one denied folder (a cloud
    /// placeholder, a stray <c>node_modules</c>) still has thousands of
    /// readable notes. The unreadable folder is skipped, the rest is listed.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ListMarkdownFiles()
    {
        if (!Directory.Exists(_vaultRoot)) return [];
        if ((new DirectoryInfo(_vaultRoot).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(L10n.Text("不支持链接的文件或文件夹。"));
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_vaultRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in ReadableEntries(() => EnumerateDirectories(directory)))
            {
                try
                {
                    var info = new DirectoryInfo(child);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0 && !info.Name.StartsWith(".", StringComparison.Ordinal))
                        pending.Push(child);
                }
                catch (Exception error) when (IsFileSystemError(error))
                {
                    // A folder that cannot even be inspected is skipped.
                }
            }
            foreach (var file in ReadableEntries(() => EnumerateMarkdownFiles(directory)))
            {
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                        result.Add(Path.GetRelativePath(_vaultRoot, file).Replace('\\', '/'));
                }
                catch (Exception error) when (IsFileSystemError(error))
                {
                    // A file that cannot be inspected is skipped.
                }
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Reads whatever could be read from one directory. The enumeration stops
    /// at the first failure, but everything read before it is kept: an
    /// unreadable entry must cost the user that entry, not the whole vault.
    /// </summary>
    private static List<string> ReadableEntries(Func<IEnumerable<string>> source)
    {
        var entries = new List<string>();
        try
        {
            foreach (var entry in source()) entries.Add(entry);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
            // The rest of this directory is unavailable; the entries already
            // read — and every other directory — stay.
        }
        return entries;
    }

    /// <summary>Reads a Markdown note by vault-relative path, with a 2 MiB bound.</summary>
    public string ReadMarkdown(string relativePath)
    {
        var path = ResolveContained(relativePath);
        if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L10n.Text("工作台只能打开 Markdown 笔记。"));
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(L10n.Text("所选笔记已不存在。"), path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(L10n.Text("不支持链接的文件或文件夹。"));
        if (info.Length > 2 * 1024 * 1024) throw new InvalidOperationException(L10n.Text("所选笔记超过 2 MiB。"));
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>
    /// Lists adjacent files and vault-contained wiki-link attachments. Existing
    /// archive notes put their originals in 附件/, rather than beside the note.
    /// Attachment bytes are never included in a model request by this reader.
    /// <para>
    /// A folder that cannot be enumerated costs the user the adjacent files
    /// only: the links the note itself carries are still resolved.
    /// </para>
    /// </summary>
    public IReadOnlyList<ObsidianAttachment> ListAttachments(string relativeMarkdownPath)
    {
        var markdown = ReadMarkdown(relativeMarkdownPath);
        var notePath = ResolveContained(relativeMarkdownPath);
        var folder = Path.GetDirectoryName(notePath)!;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ReadableEntries(() => EnumerateAllFiles(folder))) candidates.Add(path);
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
        if (!File.Exists(path)) throw new FileNotFoundException(L10n.Text("附件已不存在。"), path);
        return path;
    }

    /// <summary>
    /// Saves a new Markdown note beneath the vault analysis folder; existing
    /// files are never overwritten.
    /// <para>
    /// A name that is already taken is not a failure: the next name is tried.
    /// A write that fails is a failure: the file was created before the write,
    /// so the half-written note is removed instead of being left behind as an
    /// empty file — otherwise a single full disk would leave a trail of empty
    /// notes and the real note would never be written.
    /// </para>
    /// </summary>
    public string SaveNewAnalysis(string title, string markdown)
    {
        var safeTitle = SafeFileName(title);
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException(L10n.Text("笔记内容不能为空。"), nameof(markdown));
        var folder = ResolveContained(_analysisFolder);
        Directory.CreateDirectory(folder);
        // A folder can be replaced with a junction after initial path resolution.
        // Recheck after creation to reject any junction that is already present.
        _ = ResolveContained(_analysisFolder);
        var basePath = Path.Combine(folder, safeTitle + ".md");
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = attempt == 0 ? basePath : Path.Combine(folder, $"{safeTitle} {attempt + 1}.md");
            // The name may already belong to another note. That is not a
            // failure — it only means the next name is tried.
            if (File.Exists(candidate)) continue;
            FileStream stream;
            try
            {
                stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // The name was taken between the check and the open.
                continue;
            }
            try
            {
                using (stream)
                {
                    WriteNote(stream, markdown);
                }
                return Path.GetRelativePath(_vaultRoot, candidate).Replace('\\', '/');
            }
            catch (Exception)
            {
                // The write failed (a full disk, an antivirus lock). The file
                // is ours — it was created by the open above — so it is removed
                // rather than left behind as an empty note.
                TryDelete(candidate);
                throw;
            }
        }
        throw new IOException(L10n.Text("无法为新分析笔记选择文件名。"));
    }

    /// <summary>
    /// The file-system failures that mean "this one entry is unavailable",
    /// not "the whole operation is broken".
    /// </summary>
    private static bool IsFileSystemError(Exception error) =>
        error is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException;

    /// <summary>
    /// Removes a file this save created. Cleanup must never hide the failure
    /// that caused it, so every error here is swallowed.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
        }
    }

    private string ResolveContained(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException(L10n.Text("需要知识库内的相对路径。"), nameof(relativePath));
        var path = Path.GetFullPath(Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _vaultRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L10n.Text("路径必须位于所选知识库内。"));
        RejectReparseComponents(path);
        return path;
    }

    private void RejectReparseComponents(string path)
    {
        var rootInfo = new DirectoryInfo(_vaultRoot);
        if (rootInfo.Exists && (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(L10n.Text("不支持链接的文件或文件夹。"));

        var relative = Path.GetRelativePath(_vaultRoot, path);
        var current = _vaultRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(L10n.Text("不支持链接的文件或文件夹。"));
        }
    }

    private static string SafeRelativeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException(L10n.Text("需要填写分析文件夹。"), nameof(folder));
        var normalizedInput = folder.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedInput))
            throw new ArgumentException(L10n.Text("分析文件夹必须是知识库内的相对路径。"), nameof(folder));
        var normalized = normalizedInput.Trim('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException(L10n.Text("分析文件夹必须是知识库内的相对路径。"), nameof(folder));
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
