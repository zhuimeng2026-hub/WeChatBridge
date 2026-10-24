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
    public void ABlankTitleStillProducesAFileName()
    {
        var root = NewVault();
        try
        {
            var vault = new ObsidianVaultWorkbench(root);
            var saved = vault.SaveNewAnalysis("", "# 草稿\n\n手动整理");

            // The fallback title is localized, and the English translation of
            // 「分析草稿」 starts with a space — a file name may not.
            Assert.Equal("分析/分析草稿.md", saved);
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

    /// <summary>
    /// One unreadable folder must not take the whole listing down: the vault
    /// still shows every note it can read. The failure is injected through
    /// the enumeration seam — an ACL-denied directory is awkward to create
    /// from a test that runs as the owner of the vault.
    /// </summary>
    [Fact]
    public void AnUnreadableFolderDoesNotAbortTheVaultListing()
    {
        var root = NewVault();
        try
        {
            var unreadable = Path.Combine(root, "locked");
            Directory.CreateDirectory(unreadable);
            File.WriteAllText(Path.Combine(unreadable, "hidden.md"), "# 读不到");
            File.WriteAllText(Path.Combine(root, "readable.md"), "# 读得到");

            var vault = new VaultWithUnreadableFolder(root, unreadable);

            Assert.Equal(new[] { "readable.md" }, vault.ListMarkdownFiles());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A note whose own folder cannot be enumerated still shows the
    /// attachments the note itself links to.
    /// </summary>
    [Fact]
    public void AnUnreadableNoteFolderStillListsTheAttachmentsTheNoteLinks()
    {
        var root = NewVault();
        try
        {
            var unreadable = Path.Combine(root, "case");
            Directory.CreateDirectory(Path.Combine(unreadable, "附件"));
            File.WriteAllText(Path.Combine(unreadable, "record.md"),
                "# Record\n原始归档：[[附件/archive.zip]]");
            File.WriteAllBytes(Path.Combine(unreadable, "附件", "archive.zip"), [1, 2]);

            var vault = new VaultWithUnreadableFolder(root, unreadable);

            var attachments = vault.ListAttachments("case/record.md");

            Assert.Equal(new[] { "case/附件/archive.zip" }, attachments.Select(item => item.RelativePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A failed write must not leave a trail of empty notes behind: the file
    /// is created before the write, so the save has to remove it again.
    /// </summary>
    [Fact]
    public void AFailingWriteLeavesNoPartialNoteBehind()
    {
        var root = NewVault();
        try
        {
            var vault = new VaultWithFailingWrite(root);

            Assert.Throws<IOException>(() => vault.SaveNewAnalysis("草稿", "# 内容"));

            // Neither the half-written note nor a trail of empty ones.
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "分析")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A vault whose folder cannot be enumerated. The failure is injected
    /// through the enumeration seam: an ACL-denied directory is awkward to
    /// create from a test that runs as the owner of the vault.
    /// </summary>
    private sealed class VaultWithUnreadableFolder : ObsidianVaultWorkbench
    {
        private readonly string _unreadable;

        public VaultWithUnreadableFolder(string root, string unreadable) : base(root) => _unreadable = unreadable;

        protected override IEnumerable<string> EnumerateMarkdownFiles(string path) =>
            path.Equals(_unreadable, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("denied")
                : base.EnumerateMarkdownFiles(path);

        protected override IEnumerable<string> EnumerateAllFiles(string path) =>
            path.Equals(_unreadable, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("denied")
                : base.EnumerateAllFiles(path);
    }

    /// <summary>
    /// A vault whose write fails midway — a full disk or an antivirus lock.
    /// Neither is easy to arrange on a real disk, so the write is overridden.
    /// </summary>
    private sealed class VaultWithFailingWrite : ObsidianVaultWorkbench
    {
        public VaultWithFailingWrite(string root) : base(root) { }

        protected override void WriteNote(FileStream stream, string markdown)
        {
            stream.Write([1, 2, 3]);
            throw new IOException("disk full");
        }
    }

    private static string NewVault()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcb-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
