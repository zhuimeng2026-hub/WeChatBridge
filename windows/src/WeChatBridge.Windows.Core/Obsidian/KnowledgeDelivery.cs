using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Writes a WeChat archive into an Obsidian vault as Markdown plus a durable
/// copy of the original ZIP. Keeping the archive makes a parsing change or a
/// future converter able to rebuild the note without asking WeChat again.
/// </summary>
public static class KnowledgeDelivery
{
    public enum FailureKind { NotConfigured, UnreadableArchive }

    public sealed class FailureException : Exception
    {
        public FailureException(FailureKind kind, Exception? inner = null)
            : base(Describe(kind), inner) => Kind = kind;
        public FailureKind Kind { get; }
        private static string Describe(FailureKind kind) => kind switch
        {
            FailureKind.NotConfigured => L10n.Text("还没有选择 Obsidian 知识库文件夹。"),
            _ => L10n.Text("微信导出的文件无法读取，原始文件已保留。"),
        };
    }

    /// <summary>
    /// Delivers using the vault configured in
    /// <c>%LOCALAPPDATA%\WeChatBridge\Config\obsidian.json</c> (or
    /// <paramref name="configDirectory"/> in tests).
    /// </summary>
    public static List<string> Deliver(
        IReadOnlyList<string> paths, string? chatName, string? sceneName,
        string? configDirectory = null, CancellationToken cancellationToken = default)
    {
        var settings = new ObsidianSettingsStore(configDirectory).Load();
        if (string.IsNullOrEmpty(settings.VaultPath))
            throw new FailureException(FailureKind.NotConfigured);
        return Deliver(paths, settings.VaultPath, settings.Subfolder, chatName, sceneName, cancellationToken);
    }

    /// <summary>Delivers into an explicitly selected vault and subfolder.</summary>
    public static List<string> Deliver(
        IReadOnlyList<string> paths, string vaultPath, string subfolder,
        string? chatName, string? sceneName, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) throw new FailureException(FailureKind.UnreadableArchive);
        FolderDelivery.ValidateFolder(vaultPath);
        var folderName = DisplayName.SubfolderPath(subfolder);
        if (folderName.Length == 0) folderName = "微信流";
        var root = Path.Combine(vaultPath, folderName);
        var attachmentRoot = Path.Combine(root, "附件");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(attachmentRoot);

        var written = new List<string>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isZip = IsZipCandidate(path);
            var data = isZip ? File.ReadAllBytes(path) : [];
            WeChatNativeArchive.Transcript? transcript = null;
            string? archiveRelative = null;
            string? extractionIssue = null;
            var attachmentPaths = new List<string>();
            var referenceMap = new Dictionary<string, string>(StringComparer.Ordinal);

            if (isZip)
            {
                try
                {
                    transcript = WeChatNativeArchive.GetTranscript(data, cancellationToken);
                    if (transcript is not null
                        && Path.GetFileName(transcript.Path).Equals("聊天记录.txt", StringComparison.OrdinalIgnoreCase) == false
                        && transcript.Records is not { Count: > 0 })
                        transcript = null;
                }
                catch (OperationCanceledException) { throw; }
                catch { transcript = null; }

                var archiveFolder = Path.Combine(attachmentRoot, "原始归档");
                Directory.CreateDirectory(archiveFolder);
                var savedArchive = FolderDelivery.Save([path], archiveFolder, cancellationToken).Single();
                archiveRelative = $"原始归档/{Path.GetFileName(savedArchive)}";

                var staging = Path.Combine(Path.GetTempPath(), "wechatbridge-media-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(staging);
                    // Extract validates every entry's path, type, size and CRC. A bad archive
                    // stays safely available in 原始归档 and is explicitly called out in the note.
                    var entries = WeChatNativeArchive.Extract(data, staging, cancellationToken);
                    var sources = entries
                        .Where(name => name != transcript?.Path)
                        .ToList();
                    foreach (var sourceName in sources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourcePath = Path.Combine(staging, sourceName.Replace('/', Path.DirectorySeparatorChar));
                        var category = AttachmentCategory.FolderFor(sourceName);
                        var categoryFolder = Path.Combine(attachmentRoot, category);
                        Directory.CreateDirectory(categoryFolder);
                        var saved = FolderDelivery.Save([sourcePath], categoryFolder, cancellationToken).Single();
                        var relative = $"{category}/{Path.GetFileName(saved)}";
                        attachmentPaths.Add(relative);
                        referenceMap[sourceName] = relative;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    extractionIssue = "附件提取未完成，原始 ZIP 已保留；请检查 ZIP 是否损坏或包含不支持的内容。";
                }
                finally { TryDeleteDirectory(staging); }
            }
            else
            {
                var category = AttachmentCategory.FolderFor(Path.GetFileName(path));
                var categoryFolder = Path.Combine(attachmentRoot, category);
                Directory.CreateDirectory(categoryFolder);
                var saved = FolderDelivery.Save([path], categoryFolder, cancellationToken).Single();
                var relative = $"{category}/{Path.GetFileName(saved)}";
                attachmentPaths.Add(relative);
                referenceMap[Path.GetFileName(path)] = relative;
            }

            var sourceNameForTitle = Path.GetFileName(path);
            cancellationToken.ThrowIfCancellationRequested();
            var title = ObsidianNote.Title(chatName, transcript, sourceNameForTitle);
            cancellationToken.ThrowIfCancellationRequested();
            var markdown = ObsidianNote.Render(
                title, chatName, sceneName, DateTimeOffset.Now, transcript,
                archiveName: archiveRelative, attachments: referenceMap,
                attachmentInventory: attachmentPaths, extractionIssue: extractionIssue);
            var noteName = DisplayName.Sanitize(title) + ".md";
            var preferred = Path.Combine(root, noteName);
            if (isZip && transcript is not null && File.Exists(preferred))
            {
                var merge = ObsidianNote.TryMerge(
                    File.ReadAllText(preferred), transcript, referenceMap,
                    archiveRelative!, chatName, sceneName, DateTimeOffset.Now, out var mergedMarkdown,
                    attachmentInventory: attachmentPaths, extractionIssue: extractionIssue);
                if (merge == ObsidianNote.MergeOutcome.Merged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteAtomic(preferred, mergedMarkdown!);
                }
                if (merge != ObsidianNote.MergeOutcome.NotApplicable)
                {
                    written.Add(preferred);
                    continue;
                }
            }
            var note = UniquePath(root, noteName);
            cancellationToken.ThrowIfCancellationRequested();
            WriteAtomic(note, markdown);
            written.Add(note);
        }
        return written;
    }

    // Office Open XML documents are ZIP containers too, but are ordinary standalone
    // attachments here; only an explicitly named .zip is treated as a chat archive.
    private static bool IsZipCandidate(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Write beside the note then move: a reader never sees half a file.</summary>
    private static void WriteAtomic(string note, string markdown)
    {
        var staging = note + ".tmp";
        File.WriteAllText(staging, markdown, new UTF8Encoding(false));
        File.Move(staging, note, overwrite: true);
    }

    /// <summary>Remove only this call's private extraction staging directory.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* Temp debris must not hide the delivery result. */ }
    }

    private static string UniquePath(string folder, string name)
    {
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        var number = 1;
        while (true)
        {
            var suffix = number == 1 ? string.Empty : $" {number}";
            var candidate = Path.Combine(folder, $"{stem}{suffix}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            number++;
        }
    }
}
