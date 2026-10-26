using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Turns one verified WeChat archive into a self-contained Markdown note.
/// Ported from <c>ObsidianNote.swift</c>.
/// </summary>
public static class ObsidianNote
{
    /// <summary>The rendered message header line a note carries per record.</summary>
    private static readonly Regex MessageHeader = new(
        @"^\*\*(?<sender>.+?)\*\* · (?<stamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2})$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The line this writer appends when another archive is merged into an
    /// existing note (「追加归档：[[附件/x.zip]]（时间）」). It is written
    /// between two messages, so without excluding it here it would become
    /// part of the message above it — and that message would never match an
    /// incoming record again, so every further share would duplicate it.
    /// The marker is now also wrapped in the generated-metadata comments, so
    /// StripGeneratedMetadata drops the whole block; this constant still
    /// filters the bare line, which is what notes written before the
    /// comments existed carry.
    /// </summary>
    private const string ArchiveMarkerPrefix = "> 追加归档：";

    /// <summary>What <see cref="TryMerge"/> decided for an existing note.</summary>
    public enum MergeOutcome
    {
        /// <summary>Not a note this writer produced (or no records to merge) — the caller should pick a fresh file name.</summary>
        NotApplicable,
        /// <summary>Every incoming record is already in the note — leave it untouched.</summary>
        NothingNew,
        /// <summary>New records were appended; the out parameter holds the full replacement markdown.</summary>
        Merged,
    }
    /// <summary>
    /// Extensions macOS resolves through <c>UTType.conforms(to:)</c> — an
    /// embedded <c>![[..]]</c> link only makes sense for media Obsidian can
    /// render, everything else is a plain attachment link.
    /// </summary>
    private static readonly HashSet<string> EmbeddableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // image
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico",
        // movie
        "mp4", "mov", "m4v", "avi", "mkv", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts",
        // audio
        "mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "amr", "wma", "caf",
        // pdf
        "pdf",
    };

    /// <summary>
    /// A readable note title for Obsidian. The name captured from WeChat's title
    /// bar wins, matching the 群名 shown in 记录; transcript participants are
    /// only a fallback when OCR found nothing.
    /// </summary>
    public static string Title(
        string? chatName,
        WeChatNativeArchive.Transcript? transcript,
        string archiveName)
    {
        if (Usable(chatName) is { } usable)
            return usable.EndsWith("的聊天", StringComparison.Ordinal) ? usable : $"{usable}的聊天";
        var participants = OrderedParticipants(transcript);
        switch (participants.Count)
        {
            case 1:
                return $"{participants[0]}的聊天";
            case 2:
                return $"{participants[0]}与{participants[1]}的聊天";
            case > 2:
                return $"{participants[0]}等{participants.Count}人的聊天";
            default:
                break;
        }
        return Path.GetFileNameWithoutExtension(archiveName);
    }

    public static string Render(
        string title,
        string? chatName,
        string? sceneName,
        DateTimeOffset createdAt,
        WeChatNativeArchive.Transcript? transcript,
        string? archiveName,
        IReadOnlyDictionary<string, string>? attachments = null,
        TimeZoneInfo? timeZone = null,
        IReadOnlyList<string>? attachmentInventory = null,
        string? extractionIssue = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        string Stamp(DateTimeOffset value) => StampFor(value, zone);

        var yaml = new List<string>
        {
            "---",
            $"title: {Quoted(title)}",
            "source: WeChat",
        };
        if (!string.IsNullOrEmpty(chatName))
            yaml.Add($"chat: {Quoted(chatName)}");
        if (!string.IsNullOrEmpty(sceneName))
            yaml.Add($"scene: {Quoted(sceneName)}");
        yaml.Add($"exported: {Stamp(createdAt)}");
        if (transcript?.Records is { } counted)
            yaml.Add($"messages: {counted.Count}");
        if (archiveName is not null)
            yaml.Add($"archive: {Quoted($"附件/{archiveName}")}");
        yaml.Add("---");

        var lines = new List<string>(yaml)
        {
            "",
            $"# {title}",
            "",
            archiveName is null ? "> 来源：微信 · 独立附件" : $"> 来源：微信 · 原始归档：{AttachmentLink(archiveName)}",
        };

        if (transcript is not null)
        {
            lines.Add("");
            lines.Add("## 聊天记录");
            if (transcript.Records is { Count: > 0 } records)
            {
                foreach (var record in records)
                {
                    lines.Add("");
                    lines.Add($"**{record.Sender}** · {Stamp(record.Date)}");
                    lines.Add("");
                    lines.Add(record.Text);
                    foreach (var attachment in ReferencedAttachments(record.Text, attachments))
                    {
                        lines.Add("");
                        lines.Add(AttachmentLink(attachment));
                    }
                }
            }
            else
            {
                lines.Add("");
                lines.Add(transcript.Body);
            }
        }
        else
        {
            lines.Add("");
            lines.Add(archiveName is null
                ? "此文件为独立附件，没有聊天正文。"
                : "此 ZIP 未包含可识别的聊天正文。原始 ZIP 已保留，可在附件中打开。");
        }
        if (!string.IsNullOrEmpty(extractionIssue))
        {
            lines.Add("");
            lines.Add("<!-- wechatbridge:warning:start -->");
            lines.Add(extractionIssue);
            lines.Add("<!-- wechatbridge:warning:end -->");
        }
        if (attachmentInventory is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("<!-- wechatbridge:attachment-inventory:start -->");
            lines.Add("## 附件清单");
            foreach (var attachment in attachmentInventory.Distinct(StringComparer.Ordinal))
                lines.Add($"- {AttachmentLink(attachment)}");
            lines.Add("<!-- wechatbridge:attachment-inventory:end -->");
        }
        lines.Add("");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Re-forwarding a conversation produces overlapping archives — WeChat
    /// exports whatever messages were selected, so a second share usually
    /// repeats half of what the note already holds. Merging keeps one note
    /// per conversation: records whose (sender, minute, body) triple is
    /// already rendered in the note are skipped, new ones are appended under
    /// 聊天记录, and the front matter count/export stamp catch up. The sender
    /// alone is not a safe key — one person can send two messages in the same
    /// minute — so the body joins the comparison, which also lets a hand-edit
    /// in Obsidian resurrect a message on the next share (edits win).
    /// </summary>
    public static MergeOutcome TryMerge(
        string existingMarkdown,
        WeChatNativeArchive.Transcript transcript,
        IReadOnlyDictionary<string, string>? attachments,
        string archiveName,
        string? chatName,
        string? sceneName,
        DateTimeOffset mergedAt,
        out string? mergedMarkdown,
        TimeZoneInfo? timeZone = null,
        IReadOnlyList<string>? attachmentInventory = null,
        string? extractionIssue = null)
    {
        mergedMarkdown = null;
        if (transcript.Records is not { Count: > 0 } records)
            return MergeOutcome.NotApplicable;
        var zone = timeZone ?? TimeZoneInfo.Local;
        var normalized = existingMarkdown
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return MergeOutcome.NotApplicable;
        var closing = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closing < 0)
            return MergeOutcome.NotApplicable;

        // Generated inventory/warning blocks can occur between forwarded batches.
        // Exclude only these marked blocks from message identity parsing, while
        // leaving the actual Markdown untouched for the replacement output.
        var messageContent = StripGeneratedMetadata(normalized);
        var headers = MessageHeader.Matches(messageContent);
        var known = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Count; index++)
        {
            var bodyStart = headers[index].Index + headers[index].Length;
            var bodyEnd = index + 1 < headers.Count ? headers[index + 1].Index : messageContent.Length;
            known.Add(MessageKey(
                headers[index].Groups["sender"].Value,
                headers[index].Groups["stamp"].Value,
                messageContent[bodyStart..bodyEnd]));
        }

        var fresh = records
            .Where(record => !known.Contains(
                MessageKey(record.Sender, StampFor(record.Date, zone), record.Text)))
            .ToList();
        var newAttachments = (attachmentInventory ?? [])
            .Distinct(StringComparer.Ordinal)
            .Where(path => !normalized.Contains(AttachmentLink(path), StringComparison.Ordinal))
            .ToList();
        var newArchive = !normalized.Contains(AttachmentLink(archiveName), StringComparison.Ordinal);
        if (fresh.Count == 0 && newAttachments.Count == 0 && !newArchive && string.IsNullOrEmpty(extractionIssue))
            return MergeOutcome.NothingNew;

        var frontLines = normalized[4..closing].Split('\n').ToList();
        void Upsert(string name, string line)
        {
            var at = frontLines.FindIndex(l => l.StartsWith(name + ":", StringComparison.Ordinal));
            if (at >= 0) frontLines[at] = line; else frontLines.Add(line);
        }
        Upsert("exported", $"exported: {StampFor(mergedAt, zone)}");
        if (fresh.Count > 0 || frontLines.Any(l => l.StartsWith("messages:", StringComparison.Ordinal)))
            Upsert("messages", $"messages: {headers.Count + fresh.Count}");
        if (!string.IsNullOrEmpty(chatName)
            && !frontLines.Any(l => l.StartsWith("chat:", StringComparison.Ordinal)))
            frontLines.Add($"chat: {Quoted(chatName)}");
        if (!string.IsNullOrEmpty(sceneName)
            && !frontLines.Any(l => l.StartsWith("scene:", StringComparison.Ordinal)))
            frontLines.Add($"scene: {Quoted(sceneName)}");

        var merged = new StringBuilder(normalized.Length + 256);
        merged.Append("---\n").AppendJoin('\n', frontLines).Append("\n---");
        var body = normalized[(closing + 4)..].TrimEnd('\n');
        // Keep a single attachment inventory across incremental deliveries.
        const string inventoryEnd = "<!-- wechatbridge:attachment-inventory:end -->";
        var hasInventory = body.Contains(inventoryEnd, StringComparison.Ordinal);
        if (newAttachments.Count > 0 && hasInventory)
        {
            var newLines = string.Join("", newAttachments.Select(p => "- " + AttachmentLink(p) + "\n"));
            body = body.Replace(inventoryEnd, newLines + inventoryEnd, StringComparison.Ordinal);
        }
        merged.Append(body);
        merged.Append('\n');
        if (!normalized.Contains("\n## 聊天记录\n", StringComparison.Ordinal))
            merged.Append("\n## 聊天记录\n");
        // The marker is wrapped in the generated-metadata comments so that
        // StripGeneratedMetadata removes the whole block from message identity
        // parsing. ArchiveMarkerPrefix still filters the bare marker line, so
        // notes written before the comments existed keep the same identity.
        if (newArchive)
            merged.Append("\n<!-- wechatbridge:archive:start -->\n").Append(ArchiveMarkerPrefix)
                .Append(AttachmentLink(archiveName))
                .Append('（').Append(StampFor(mergedAt, zone)).Append('）').Append("\n<!-- wechatbridge:archive:end -->\n");
        foreach (var record in fresh)
        {
            merged.Append("\n**").Append(record.Sender).Append("** · ")
                .Append(StampFor(record.Date, zone)).Append("\n\n")
                .Append(record.Text).Append('\n');
            foreach (var attachment in ReferencedAttachments(record.Text, attachments))
                merged.Append('\n').Append(AttachmentLink(attachment)).Append('\n');
        }
        if (newAttachments.Count > 0 && !hasInventory)
        {
            merged.Append("\n<!-- wechatbridge:attachment-inventory:start -->\n## 附件清单\n");
            foreach (var attachment in newAttachments)
                merged.Append("- ").Append(AttachmentLink(attachment)).Append('\n');
            merged.Append("<!-- wechatbridge:attachment-inventory:end -->\n");
        }
        if (!string.IsNullOrEmpty(extractionIssue))
            merged.Append("\n<!-- wechatbridge:warning:start -->\n")
                .Append(extractionIssue).Append("\n<!-- wechatbridge:warning:end -->\n");
        mergedMarkdown = merged.ToString();
        return MergeOutcome.Merged;
    }

    /// <summary>
    /// One record's identity in a note: sender, minute, and the body stripped
    /// of blank edges and any wiki-link lines this writer appended beside it.
    /// The merge marker is stripped too: it is written between two messages,
    /// so it belongs to the message above it — and a message whose body
    /// carries it never matches the incoming record again, which made every
    /// further share of the same chat duplicate one more message.
    /// </summary>
    private static string MessageKey(string sender, string stamp, string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0
                && !line.StartsWith("![[", StringComparison.Ordinal)
                && !line.StartsWith("[[", StringComparison.Ordinal)
                && !line.StartsWith(ArchiveMarkerPrefix, StringComparison.Ordinal))
            .ToList();
        return $"{sender.Trim()}\n{stamp}\n{string.Join('\n', lines)}";
    }

    private static string StripGeneratedMetadata(string markdown)
    {
        var withoutInventory = Regex.Replace(markdown,
            @"(?s)<!-- wechatbridge:attachment-inventory:start -->.*?<!-- wechatbridge:attachment-inventory:end -->", "");
        var withoutWarnings = Regex.Replace(withoutInventory,
            @"(?s)<!-- wechatbridge:warning:start -->.*?<!-- wechatbridge:warning:end -->", "");
        return Regex.Replace(withoutWarnings,
            @"(?s)<!-- wechatbridge:archive:start -->.*?<!-- wechatbridge:archive:end -->", "");
    }

    private static string StampFor(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Longest UTF-8 name wins: 图片_1.jpg must not steal the hit that belongs
    /// to 图片_11.jpg sitting inside the same message.
    /// </summary>
    private static List<string> ReferencedAttachments(
        string text,
        IReadOnlyDictionary<string, string>? available)
    {
        var result = new List<string>();
        if (available is null || available.Count == 0)
            return result;
        var remaining = text;
        var ordered = available.Keys
            .OrderByDescending(k => Encoding.UTF8.GetByteCount(k))
            .ThenBy(k => k, StringComparer.Ordinal);
        var leafCounts = available.Keys.GroupBy(key => Path.GetFileName(key) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var name in ordered)
        {
            var leaf = Path.GetFileName(name);
            var match = remaining.Contains(name, StringComparison.Ordinal)
                || (leafCounts.GetValueOrDefault(leaf) == 1 && remaining.Contains(leaf, StringComparison.Ordinal));
            if (name.Length == 0 || !match || !available.TryGetValue(name, out var saved))
            {
                continue;
            }
            result.Add(saved);
            remaining = remaining.Replace(name, "", StringComparison.Ordinal);
        }
        return result;
    }

    private static string AttachmentLink(string name)
    {
        var path = $"附件/{name}"
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("^", "\\^", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
        var extension = Path.GetExtension(name).TrimStart('.');
        return EmbeddableExtensions.Contains(extension) ? $"![[{path}]]" : $"[[{path}]]";
    }

    private static string Quoted(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static List<string> OrderedParticipants(WeChatNativeArchive.Transcript? transcript)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var participants = new List<string>();
        foreach (var record in transcript?.Records ?? [])
        {
            var sender = record.Sender.Trim();
            if (sender.Length > 0 && seen.Add(sender))
                participants.Add(sender);
        }
        return participants;
    }

    private static string? Usable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
