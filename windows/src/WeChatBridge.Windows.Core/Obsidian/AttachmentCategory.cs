namespace WeChatBridge.Windows.Core;

/// <summary>Stable folder names used for saved chat attachments.</summary>
public static class AttachmentCategory
{
    /// <summary>
    /// Folder for the files Obsidian must not list: the raw WeChat ZIP and
    /// anything technical that arrived with a client's files.
    /// <para>
    /// The leading dot is the whole mechanism. Obsidian skips every path whose
    /// segment starts with one — no plugin, no CSS, no patch to Obsidian — so
    /// these files stay on disk, keep their path, and stay out of the file
    /// explorer, the link index, search and quick switcher. Deliberately a
    /// native behaviour rather than a UI filter: a vault configured this way
    /// works on any Obsidian version and on any machine the folder is copied
    /// to, including one where the customer has never opened the settings.
    /// </para>
    /// </summary>
    public const string HiddenOriginalsFolder = ".原始归档";

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico",
    };
    private static readonly HashSet<string> Word = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "docm", "dot", "dotx", "dotm", "rtf",
    };
    private static readonly HashSet<string> Pdf = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf",
    };
    private static readonly HashSet<string> Spreadsheets = new(StringComparer.OrdinalIgnoreCase)
    {
        "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "xltm", "csv", "tsv", "ods", "et",
    };
    private static readonly HashSet<string> Presentations = new(StringComparer.OrdinalIgnoreCase)
    {
        "ppt", "pptx", "pptm", "pot", "potx", "potm", "pps", "ppsx", "ppsm", "odp",
    };
    private static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "rst", "log",
    };
    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "wma", "amr", "aiff",
    };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mov", "m4v", "avi", "mkv", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts",
    };

    /// <summary>
    /// Extensions a lawyer never works with: what a file arrived in, not what
    /// it says. Kept narrow on purpose — an unknown document format from a
    /// client (<c>ofd</c>, <c>eml</c>, <c>wps</c>) must stay in <c>其他</c> and
    /// stay visible, because it may well be the case file.
    /// </summary>
    private static readonly HashSet<string> Technical = new(StringComparer.OrdinalIgnoreCase)
    {
        // Archives.
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz",
        // Installers and binaries.
        "msi", "msix", "appx", "exe", "dll", "sys", "cab", "iso",
        // Scripts and system leftovers.
        "bat", "cmd", "ps1", "vbs", "reg", "tmp", "bak", "lnk",
    };

    public static string FolderFor(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (Images.Contains(extension)) return "图片";
        if (Word.Contains(extension)) return "Word文档";
        if (Pdf.Contains(extension)) return "PDF";
        if (Spreadsheets.Contains(extension)) return "电子表格";
        if (Presentations.Contains(extension)) return "演示文稿";
        if (Texts.Contains(extension)) return "文本";
        if (Audios.Contains(extension)) return "音频";
        if (Videos.Contains(extension)) return "视频";
        return "其他";
    }

    /// <summary>
    /// True when the file is packaging or machinery rather than content, so it
    /// belongs in <see cref="HiddenOriginalsFolder"/> instead of a category a
    /// reader browses.
    /// </summary>
    public static bool IsTechnical(string fileName) =>
        Technical.Contains(Path.GetExtension(fileName).TrimStart('.'));
}
