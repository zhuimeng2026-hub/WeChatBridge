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
/// What goes in: the allowlisted log files and a plain-text environment
/// summary. Log lines may contain local paths and exception context, so the
/// summary asks the user to inspect the bundle before sharing it.
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
            Directory.CreateDirectory(paths.Logs);
            var target = string.IsNullOrWhiteSpace(destinationDirectory)
                ? paths.Logs
                : destinationDirectory;
            Directory.CreateDirectory(target);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var bundle = Path.Combine(target, $"{BundlePrefix}-{stamp}-{Guid.NewGuid():N}.zip");

            var completed = false;
            var created = false;
            try
            {
                var snapshot = DiagnosticLog.CreateSnapshot(paths);
                using (var output = new FileStream(bundle, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    created = true;
                    using var archive = new ZipArchive(output, ZipArchiveMode.Create);
                    foreach (var file in snapshot)
                    {
                        if (file.Content is not null)
                            AddBytes(archive, file.Name, file.Content);
                        else if (file.Error is not null)
                            AddText(archive, $"日志读取说明/{file.Name}.txt", $"未能读取 {file.Name}：{file.Error}");
                        else
                            AddText(archive, $"日志读取说明/{file.Name}.txt", $"未找到 {file.Name}。导出时该日志文件不存在。");
                    }
                    AddText(archive, "环境摘要.txt", BuildSummary(paths, snapshot.Any(file => file.Truncated)));
                }
                completed = true;
                return bundle;
            }
            finally
            {
                if (!completed && created)
                    try { File.Delete(bundle); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            }
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
    private static string BuildSummary(InboxPaths paths, bool logWasTruncated)
    {
        var builder = new StringBuilder();
        builder.AppendLine("律档通 诊断信息");
        builder.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"数据目录: {paths.Root}");
        builder.AppendLine($"日志目录: {paths.Logs}");
        builder.AppendLine();
        builder.AppendLine("说明：");
        builder.AppendLine("- windows.log 是程序运行日志，包含发生错误时的完整异常与调用堆栈。");
        builder.AppendLine("- 日志用于记录运行状态和错误，可能包含本机路径及异常上下文；分享前请检查压缩包内容。");
        if (logWasTruncated)
            builder.AppendLine("- 至少一个日志文件超过 4 MiB；压缩包仅保留了该文件的末尾部分。");
        return builder.ToString();
    }

    private static void AddBytes(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var target = entry.Open();
        target.Write(content);
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
