using System.IO.Compression;
using System.Text;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class KnowledgeDeliveryTests
{
    [Fact]
    public void ExtractsCategorizedAttachmentsAndRetainsNestedDuplicateNames()
    {
        using var fixture = new TempInbox();
        var source = Zip(fixture, "chat.zip", [
            ("聊天记录.txt", "·甲\n2026年9月20日 09:10\n见 photo.png\n"),
            ("a/photo.png", "image-a"),
            ("b/photo.png", "image-b"),
            ("spec.DOCX", "word"),
            ("data.CsV", "sheet"),
            ("clip.MP4", "video"),
            ("notes.xyz", "other"),
        ]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var notePath = KnowledgeDelivery.Deliver([source], destination, "项目", "群", null).Single();
        var note = File.ReadAllText(notePath);
        var attachmentRoot = Path.Combine(destination, "项目", "附件");
        Assert.Equal("image-a", File.ReadAllText(Path.Combine(attachmentRoot, "图片", "photo.png")));
        Assert.Equal("image-b", File.ReadAllText(Path.Combine(attachmentRoot, "图片", "photo (2).png")));
        Assert.True(File.Exists(Path.Combine(attachmentRoot, "Word文档", "spec.DOCX")));
        Assert.True(File.Exists(Path.Combine(attachmentRoot, "电子表格", "data.CsV")));
        Assert.True(File.Exists(Path.Combine(attachmentRoot, "视频", "clip.MP4")));
        Assert.True(File.Exists(Path.Combine(attachmentRoot, "其他", "notes.xyz")));
        Assert.True(File.Exists(Path.Combine(attachmentRoot, "原始归档", "chat.zip")));
        Assert.Contains("[[附件/图片/photo.png]]", note);
        Assert.Contains("[[附件/图片/photo (2).png]]", note);
        Assert.Contains("[[附件/Word文档/spec.DOCX]]", note);
        Assert.Contains("[[附件/电子表格/data.CsV]]", note);
        Assert.Contains("[[附件/视频/clip.MP4]]", note);
        Assert.Contains("[[附件/其他/notes.xyz]]", note);
        Assert.Contains("[[附件/原始归档/chat.zip]]", note);
    }

    [Fact]
    public void AttachmentOnlyZipKeepsReadmeTxtAndDoesNotInventChatBody()
    {
        using var fixture = new TempInbox();
        var source = Zip(fixture, "materials.zip", [("readme.txt", "project notes"), ("asset.bin", "bytes")]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var note = File.ReadAllText(KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single());
        Assert.Contains("未包含可识别的聊天正文", note);
        Assert.DoesNotContain("## 聊天记录", note);
        // A .txt attachment is a text file, so it lands in 文本; a .bin has no
        // known type and stays in 其他.
        Assert.Contains("[[附件/文本/readme.txt]]", note);
        Assert.Contains("[[附件/其他/asset.bin]]", note);
        Assert.True(File.Exists(Path.Combine(destination, "项目", "附件", "文本", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "项目", "附件", "其他", "asset.bin")));
    }

    [Fact]
    public void StandaloneFileIsCategorizedAndRepeatedSavesDoNotOverwrite()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("photo.JpG", "original");
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var first = KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single();
        var second = KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single();
        var attachments = Path.Combine(destination, "项目", "附件", "图片");
        Assert.Equal("original", File.ReadAllText(Path.Combine(attachments, "photo.JpG")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(attachments, "photo (2).JpG")));
        Assert.Contains("[[附件/图片/photo.JpG]]", File.ReadAllText(first));
        Assert.Contains("[[附件/图片/photo (2).JpG]]", File.ReadAllText(second));
    }

    [Fact]
    public void InvalidZipIsRetainedAndExplicitlyReportsExtractionFailure()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("broken.zip", "not a zip");
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var notePath = KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single();
        var note = File.ReadAllText(notePath);
        Assert.Contains("附件提取未完成", note);
        Assert.DoesNotContain("附件已单独归档", note);
        Assert.True(File.Exists(Path.Combine(destination, "项目", "附件", "原始归档", "broken.zip")));
        Assert.DoesNotContain("## 附件清单", note);
    }

    [Fact]
    public void PathTraversalZipIsRejectedAndMergeReportsFailureWithoutLosingSavedFiles()
    {
        using var fixture = new TempInbox();
        const string transcript = "·甲\n2026年9月20日 09:10\n同一条消息\n";
        var first = Zip(fixture, "first.zip", [("聊天记录.txt", transcript)]);
        var malicious = Zip(fixture, "malicious.zip", [
            ("聊天记录.txt", transcript),
            ("safe.jpg", "safe"),
            ("../escape.txt", "escape"),
        ]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var notePath = KnowledgeDelivery.Deliver([first], destination, "项目", "群", null).Single();
        KnowledgeDelivery.Deliver([malicious], destination, "项目", "群", null);
        var note = File.ReadAllText(notePath);
        Assert.Contains("附件提取未完成", note);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "项目", "附件", "其他", "escape.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "项目", "附件", "图片", "safe.jpg")));
        Assert.True(File.Exists(Path.Combine(destination, "项目", "附件", "原始归档", "malicious.zip")));
        Assert.Equal(1, note.Split("同一条消息").Length - 1);
    }

    [Theory]
    [InlineData("report.docx", "Word文档")]
    [InlineData("table.xlsx", "电子表格")]
    public void OfficeZipContainersRemainStandaloneAttachments(string name, string category)
    {
        using var fixture = new TempInbox();
        var officeEntries = category == "Word文档"
            ? new[] { ("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<document/>") }
            : new[] { ("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<workbook/>") };
        var source = Zip(fixture, name, officeEntries);
        var officeContainer = File.ReadAllBytes(source);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var note = File.ReadAllText(KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single());
        var saved = Path.Combine(destination, "项目", "附件", category, name);
        Assert.Equal(officeContainer, File.ReadAllBytes(saved));
        Assert.Contains($"[[附件/{category}/{name}]]", note);
        Assert.DoesNotContain("原始归档", note);
    }

    /// <summary>
    /// The product naming decision (docs/PRODUCT_NAMING_ZH.md §2) asks for PDF,
    /// PowerPoint, plain text and audio to be told apart from each other, not
    /// pooled into 其他 — a PDF is the most common document a lawyer receives.
    /// The extension test is case-insensitive, and a name with no extension is
    /// not guessed at.
    /// </summary>
    [Theory]
    [InlineData("contract.pdf", "PDF")]
    [InlineData("CONTRACT.PDF", "PDF")]
    [InlineData("deck.pptx", "演示文稿")]
    [InlineData("slides.ppt", "演示文稿")]
    [InlineData("notes.txt", "文本")]
    [InlineData("readme.md", "文本")]
    [InlineData("voice.mp3", "音频")]
    [InlineData("memo.wav", "音频")]
    [InlineData("unknown.xyz", "其他")]
    [InlineData("noextension", "其他")]
    public void EachDocumentKindGetsItsOwnFolder(string name, string category)
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource(name, "payload");
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var note = File.ReadAllText(KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single());
        Assert.True(File.Exists(Path.Combine(destination, "项目", "附件", category, name)));
        Assert.Contains($"[[附件/{category}/{name}]]", note);
    }

    /// <summary>
    /// Every kind has to be its own folder, not a bucket of several: a folder
    /// that mixes a PDF with a spreadsheet is no better than no classification
    /// at all. One archive carrying all of them proves the split.
    /// </summary>
    [Fact]
    public void OneArchiveSplitsEveryKindIntoItsOwnFolder()
    {
        using var fixture = new TempInbox();
        var source = Zip(fixture, "mixed.zip", [
            ("photo.png", "image"),
            ("memo.docx", "word"),
            ("contract.pdf", "pdf"),
            ("table.xlsx", "sheet"),
            ("deck.pptx", "slides"),
            ("notes.txt", "text"),
            ("voice.mp3", "audio"),
            ("clip.mp4", "video"),
            ("mystery.xyz", "other"),
        ]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var note = File.ReadAllText(KnowledgeDelivery.Deliver([source], destination, "项目", null, null).Single());
        var attachments = Path.Combine(destination, "项目", "附件");
        foreach (var (name, category) in new[]
        {
            ("photo.png", "图片"), ("memo.docx", "Word文档"), ("contract.pdf", "PDF"),
            ("table.xlsx", "电子表格"), ("deck.pptx", "演示文稿"), ("notes.txt", "文本"),
            ("voice.mp3", "音频"), ("clip.mp4", "视频"), ("mystery.xyz", "其他"),
        })
        {
            Assert.True(File.Exists(Path.Combine(attachments, category, name)), $"{category}/{name}");
            Assert.Contains($"[[附件/{category}/{name}]]", note);
        }
    }

    [Fact]
    public void MergeAddsNewAttachmentsEvenWhenTranscriptHasNoNewMessages()
    {
        using var fixture = new TempInbox();
        const string transcript = "·甲\n2026年9月20日 09:10\n同一条消息\n";
        var first = Zip(fixture, "first.zip", [("聊天记录.txt", transcript), ("one.jpg", "one")]);
        var second = Zip(fixture, "second.zip", [("聊天记录.txt", transcript), ("two.jpg", "two")]);
        var third = Zip(fixture, "third.zip", [("聊天记录.txt", transcript), ("three.jpg", "three")]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var notePath = KnowledgeDelivery.Deliver([first], destination, "项目", "群", null).Single();
        var mergedPath = KnowledgeDelivery.Deliver([second], destination, "项目", "群", null).Single();
        KnowledgeDelivery.Deliver([third], destination, "项目", "群", null);
        Assert.Equal(notePath, mergedPath);
        var note = File.ReadAllText(notePath);
        Assert.Contains("[[附件/图片/one.jpg]]", note);
        Assert.Contains("[[附件/图片/two.jpg]]", note);
        Assert.Contains("[[附件/图片/three.jpg]]", note);
        Assert.Equal(1, note.Split("同一条消息").Length - 1);
        Assert.Contains("messages: 1", note);
    }

    [Fact]
    public void MergeTracksAnotherArchiveEvenWhenAllMessagesAndAttachmentsRepeat()
    {
        using var fixture = new TempInbox();
        const string transcript = "·甲\n2026年9月20日 09:10\n同一条消息\n";
        var first = Zip(fixture, "first.zip", [("聊天记录.txt", transcript)]);
        var second = Zip(fixture, "second.zip", [("聊天记录.txt", transcript)]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var notePath = KnowledgeDelivery.Deliver([first], destination, "项目", "群", null).Single();
        KnowledgeDelivery.Deliver([second], destination, "项目", "群", null);
        var note = File.ReadAllText(notePath);
        Assert.Contains("[[附件/原始归档/first.zip]]", note);
        Assert.Contains("[[附件/原始归档/second.zip]]", note);
        Assert.Equal(1, note.Split("同一条消息").Length - 1);
    }

    [Fact]
    public void MergeKeepsOneAttachmentInventoryHeadingAcrossRepeatedImports()
    {
        using var fixture = new TempInbox();
        const string transcript = "·甲\n2026年9月20日 09:10\n同一条消息\n";
        var first = Zip(fixture, "first.zip", [("聊天记录.txt", transcript), ("one.jpg", "one")]);
        var second = Zip(fixture, "second.zip", [("聊天记录.txt", transcript), ("two.jpg", "two")]);
        var third = Zip(fixture, "third.zip", [("聊天记录.txt", transcript), ("three.jpg", "three")]);
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);

        var notePath = KnowledgeDelivery.Deliver([first], destination, "项目", "群", null).Single();
        KnowledgeDelivery.Deliver([second], destination, "项目", "群", null);
        KnowledgeDelivery.Deliver([third], destination, "项目", "群", null);
        var note = File.ReadAllText(notePath);
        Assert.Equal(1, note.Split("## 附件清单").Length - 1);
        Assert.Contains("[[附件/图片/one.jpg]]", note);
        Assert.Contains("[[附件/图片/two.jpg]]", note);
        Assert.Contains("[[附件/图片/three.jpg]]", note);
        Assert.Equal(1, note.Split("同一条消息").Length - 1);
    }

    [Fact]
    public void CancellationPropagates()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("photo.jpg", "image");
        var destination = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(destination);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            KnowledgeDelivery.Deliver([source], destination, "项目", null, null, cancellation.Token));
    }

    private static string Zip(TempInbox fixture, string name, IReadOnlyList<(string Path, string Text)> entries)
    {
        var path = fixture.WriteSource(name, "");
        using var stream = File.Open(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryPath, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }
}
