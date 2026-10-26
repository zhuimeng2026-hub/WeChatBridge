using System.IO;
using System.IO.Compression;
using System.Text;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Packs the diagnostic log into a file the user can hand over.
/// <para>
/// The log is the evidence, and evidence the user cannot get to is evidence
/// nobody sees. Asking someone to find <c>Data\Inbox\Logs\windows.log</c> on
/// a machine they do not know is asking them to do the diagnosis.
/// </para>
/// <para>
/// What goes in: the log and its rolled-over generations, plus a plain-text
/// summary of the environment. What stays out: chat content, note bodies,
/// licences, tokens and anything else that is the user's data rather than the
/// product's behaviour. A diagnostic bundle someone is willing to send is
/// worth more than a complete one they are not.
/// </para>
/// </summary>
internal static class DiagnosticExportService
{
    /// <summary>The name of the bundle, without a timestamp.</summary>
    public const string BundlePrefix = "律档通-诊断日志";

    /// <summary>
    /// Writes a bundle next to the log and returns its path.
    /// </summary>
    /// <param name="paths">Where the log lives.</param>
    /// <param name="destinationDirectory">
    /// Where to put the bundle. Null means the log's own directory.
    /// </param>
    /// <param name="error">Why the export failed, when it did.</param>
    public static string? Export(InboxPaths paths, string? destinationDirectory, out string? error)
    {
        error = null;
        try
        {
            paths.EnsureCreated();
            var target = string.IsNullOrWhiteSpace(destinationDirectory)
                ? paths.Logs
                : destinationDirectory;
            Directory.CreateDirectory(target);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bundle = Path.Combine(target, $"{BundlePrefix}-{stamp}.zip");

            using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
            {
                // The log and its rolled-over generations, newest first.
                foreach (var file in Directory
                    .EnumerateFiles(paths.Logs, DiagnosticLog.FileName + "*")
                    .OrderByDescending(f => f))
                {
                    Add(archive, file, Path.GetFileName(file));
                }

                // A readable summary, so whoever reads the bundle does not have
                // to know the log format to start.
                AddText(archive, "环境摘要.txt", BuildSummary(paths));
            }

            return bundle;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidOperationException)
        {
            error = failure.Message;
            return null;
        }
    }

    /// <summary>
    /// The environment, in the form the user would have to describe it if
    /// asked what their machine looks like.
    /// </summary>
    private static string BuildSummary(InboxPaths paths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("律档通 诊断信息");
        builder.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"数据目录: {paths.Root}");
        builder.AppendLine($"日志目录: {paths.Logs}");
        builder.AppendLine();
        builder.AppendLine("说明：");
        builder.AppendLine("- windows.log 是程序运行日志，包含发生错误时的完整异常与调用堆栈。");
        builder.AppendLine("- 本压缩包不包含聊天内容、笔记正文、账号或许可证信息。");
        return builder.ToString();
    }

    private static void Add(ZipArchive archive, string file, string entryName)
    {
        try
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var source = File.OpenRead(file);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // One unreadable file must not cost the user the whole bundle.
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
