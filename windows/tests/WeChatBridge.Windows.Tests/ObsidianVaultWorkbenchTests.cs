using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class ObsidianVaultWorkbenchTests
{
    [Fact]
    public void ListsAndReadsMarkdownNotesRelativeToTheVault()
    {
        var root = NewVault();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "案例"));
            File.WriteAllText(Path.Combine(root, "案例", "沟通记录.md"), "# 沟通记录\n内容");
            File.WriteAllText(Path.Combine(root, "ignore.txt"), "not markdown");

            var vault = new ObsidianVaultWorkbench(root);

            Assert.Equal(new[] { "案例/沟通记录.md" }, vault.ListMarkdownFiles());
            Assert.Equal("# 沟通记录\n内容", vault.ReadMarkdown("案例/沟通记录.md"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SavesANewSanitizedNoteWithoutOverwritingAnExistingNote()
    {
        var root = NewVault();
        try
        {
            var vault = new ObsidianVaultWorkbench(root);
            var first = vault.SaveNewAnalysis("../材料:整理", "# 草稿\n\n手动整理");
            var second = vault.SaveNewAnalysis("../材料:整理", "# 第二份");

            Assert.Equal("分析/材料-整理.md", first);
            Assert.Equal("分析/材料-整理 2.md", second);
            Assert.Equal("# 草稿\n\n手动整理", File.ReadAllText(Path.Combine(root, first.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal("# 第二份", File.ReadAllText(Path.Combine(root, second.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsTraversalAndNonMarkdownReads()
    {
        var root = NewVault();
        try
        {
            var vault = new ObsidianVaultWorkbench(root);

            Assert.Throws<InvalidOperationException>(() => vault.ReadMarkdown("../outside.txt"));
            Assert.Throws<InvalidOperationException>(() => vault.ReadMarkdown("../outside.md"));
            Assert.Throws<InvalidOperationException>(() => vault.ReadMarkdown("outside.txt"));
            Assert.Throws<ArgumentException>(() => new ObsidianVaultWorkbench(root, "../outside"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ListsAdjacentAttachmentsWithoutReadingTheirContents()
    {
        var root = NewVault();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "case"));
            File.WriteAllText(Path.Combine(root, "case", "record.md"), "# Record");
            File.WriteAllBytes(Path.Combine(root, "case", "contract.pdf"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "case", "photo.png"), [4, 5]);

            var attachments = new ObsidianVaultWorkbench(root).ListAttachments("case/record.md");

            Assert.Equal(new[] { "contract.pdf", "photo.png" }, attachments.Select(item => item.Name));
            Assert.Equal(new long[] { 3, 2 }, attachments.Select(item => item.ByteCount));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsOversizedNotesAndAbsoluteSaveFolders()
    {
        var root = NewVault();
        try
        {
            var source = Path.Combine(root, "large.md");
            using (var stream = File.Create(source)) stream.SetLength(2 * 1024 * 1024 + 1);
            var vault = new ObsidianVaultWorkbench(root);

            Assert.Throws<InvalidOperationException>(() => vault.ReadMarkdown("large.md"));
            Assert.Throws<ArgumentException>(() => new ObsidianVaultWorkbench(root, Path.GetTempPath()));
            Assert.Throws<ArgumentException>(() => vault.ReadMarkdown(source));
            Assert.Throws<ArgumentException>(() => vault.SaveNewAnalysis("blank", " "));
            Assert.False(Directory.Exists(Path.Combine(root, "分析")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OmitsConfigurationDirectoriesAndKeepsNestedAnalysisInsideVault()
    {
        var root = NewVault();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".obsidian"));
            File.WriteAllText(Path.Combine(root, ".obsidian", "private.md"), "configuration");
            var vault = new ObsidianVaultWorkbench(root, "case/分析");
            var saved = vault.SaveNewAnalysis("result", "# Result");

            Assert.Equal("case/分析/result.md", saved);
            Assert.Equal(new[] { saved }, vault.ListMarkdownFiles());
            Assert.Equal("# Result", vault.ReadMarkdown(saved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindsExporterAttachmentsInSubfoldersWithoutFollowingOutsideLinks()
    {
        var root = NewVault();
        try
        {
            var caseFolder = Path.Combine(root, "case");
            Directory.CreateDirectory(Path.Combine(caseFolder, "附件"));
            File.WriteAllText(Path.Combine(caseFolder, "record.md"),
                "# Record\n原始归档：[[附件/archive.zip]]\n![[附件/photo.png|照片]]\n[[../../outside.pdf]]");
            File.WriteAllBytes(Path.Combine(caseFolder, "附件", "archive.zip"), [1]);
            File.WriteAllBytes(Path.Combine(caseFolder, "附件", "photo.png"), [2, 3]);

            var attachments = new ObsidianVaultWorkbench(root).ListAttachments("case/record.md");

            Assert.Equal(new[] { "case/附件/archive.zip", "case/附件/photo.png" },
                attachments.Select(item => item.RelativePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewVault()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcb-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
