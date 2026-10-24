using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Panes;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The workbench reader loads a note and lists its attachments as two
/// separate steps, because they fail differently: a folder that cannot be
/// enumerated must cost the user the attachments, not the note they just
/// opened.
/// </summary>
public sealed class WorkbenchNoteTests
{
    [Fact]
    public void AFailingAttachmentListingKeepsTheNote()
    {
        var note = WorkbenchNote.Load(
            () => "# 沟通记录\n\n设备已经送达东区仓库。",
            () => throw new UnauthorizedAccessException("denied"));

        Assert.Contains("设备已经送达", note.Markdown);
        Assert.NotNull(note.AttachmentError);
        Assert.Empty(note.Attachments);
    }

    [Fact]
    public void AReadableNoteCarriesItsAttachments()
    {
        var note = WorkbenchNote.Load(
            () => "# 沟通记录",
            () => [new ObsidianAttachment("case/合同.pdf", "合同.pdf", 12)]);

        Assert.Null(note.AttachmentError);
        Assert.Single(note.Attachments);
        Assert.Equal("case/合同.pdf", note.Attachments[0].RelativePath);
    }

    [Fact]
    public void ANoteThatCannotBeReadThrows()
    {
        // Nothing to show: the reader clears the document and reports the
        // failure, so the read itself keeps failing loudly.
        Assert.Throws<FileNotFoundException>(() => WorkbenchNote.Load(
            () => throw new FileNotFoundException("所选笔记已不存在。"),
            () => []));
    }
}
