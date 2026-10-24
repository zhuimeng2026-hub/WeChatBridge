using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// A note as the workbench reader shows it: the Markdown, the attachments,
/// and the failure of the attachment listing — if there was one.
/// </summary>
public sealed record WorkbenchNote(
    string Markdown,
    IReadOnlyList<ObsidianAttachment> Attachments,
    Exception? AttachmentError)
{
    /// <summary>
    /// Reads a note and lists its attachments, keeping the note when the
    /// attachment listing fails.
    /// <para>
    /// The two steps fail differently: a note that cannot be read leaves
    /// nothing to show, so the failure travels on. A folder that cannot be
    /// enumerated costs the user the attachments only — the note was read, so
    /// it stays on screen and the pane reports the listing problem beside it.
    /// </para>
    /// </summary>
    public static WorkbenchNote Load(
        Func<string> readMarkdown,
        Func<IReadOnlyList<ObsidianAttachment>> listAttachments)
    {
        var markdown = readMarkdown();
        try
        {
            return new WorkbenchNote(markdown, listAttachments(), null);
        }
        catch (Exception error)
        {
            return new WorkbenchNote(markdown, [], error);
        }
    }
}
