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
    private static readonly HashSet<string> Spreadsheets = new(StringComparer.OrdinalIgnoreCase)
    {
        "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "xltm", "csv", "tsv", "ods", "et",
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
        if (Spreadsheets.Contains(extension)) return "电子表格";
        if (Videos.Contains(extension)) return "视频";
        return "其他";
    }
}
