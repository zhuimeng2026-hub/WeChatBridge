namespace WeChatBridge.Windows.Core;

/// <summary>Stable folder names used for saved chat attachments.</summary>
public static class AttachmentCategory
{
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
}
