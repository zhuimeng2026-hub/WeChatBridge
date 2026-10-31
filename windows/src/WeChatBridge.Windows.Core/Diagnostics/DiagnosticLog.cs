using System.Security.Cryptography;
using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>How serious a log line is.</summary>
public enum LogLevel { Info, Warn, Error }

/// <summary>One file in a coordinated diagnostic-log snapshot.</summary>
public sealed record DiagnosticLogSnapshotFile(string Name, byte[]? Content, string? Error, bool Truncated = false)
{
    public bool IsMissing => Content is null && Error is null;
}

/// <summary>
/// Bounded, best-effort diagnostic logging shared by the main app and ShareTarget.
/// Writes, rollover and snapshots coordinate through a named mutex derived from
/// the normalized log path, so independent processes see one serialized stream.
/// </summary>
public static class DiagnosticLog
{
    public const string FileName = "windows.log";
    /// <summary>Maximum UTF-8 bytes per complete event, including its LF terminator.</summary>
    public const int MaxEventBytes = 64 * 1024;
    private const long MaxBytes = 4 * 1024 * 1024;
    private const int MaxSnapshotFileBytes = 4 * 1024 * 1024;
    private const int KeepGenerations = 3;
    private const int MutexWaitMilliseconds = 2000;
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void Write(InboxPaths paths, LogLevel level, string message, Exception? error = null)
    {
        try
        {
            Directory.CreateDirectory(paths.Logs);
            var file = Path.GetFullPath(Path.Combine(paths.Logs, FileName));
            var line = Format(level, message, error);
            var bytes = Utf8.GetBytes(line + "\n");
            using var mutex = CreateMutex(file);
            if (!TryEnter(mutex)) return;
            try
            {
                var length = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (length + bytes.Length > MaxBytes)
                {
                    // If rollover fails, skip this event instead of exceeding
                    // the cap or truncating retained evidence.
                    if (!Roll(file)) return;
                }
                // Append through one handle while holding the path mutex. The
                // predicted bound prevents a failed rollover growing forever.
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch { /* Logging must never take down the feature that logged. */ }
    }

    public static void Info(InboxPaths paths, string message) => Write(paths, LogLevel.Info, message);
    public static void Info(InboxPaths paths, string message, Exception? error) => Write(paths, LogLevel.Info, message, error);
    public static void Warn(InboxPaths paths, string message, Exception? error = null) => Write(paths, LogLevel.Warn, message, error);
    public static void Error(InboxPaths paths, string message, Exception? error = null) => Write(paths, LogLevel.Error, message, error);

    /// <summary>
    /// Copies the supported log set while holding the same mutex as writes and
    /// rollover. Compression should happen after this method returns.
    /// Missing files have neither content nor error; unreadable files carry an
    /// error string so callers can report the omission.
    /// </summary>
    public static IReadOnlyList<DiagnosticLogSnapshotFile> CreateSnapshot(InboxPaths paths)
    {
        var files = Enumerable.Range(0, KeepGenerations + 1)
            .Select(i => i == 0 ? FileName : $"{FileName}.{i}").ToArray();
        try
        {
            Directory.CreateDirectory(paths.Logs);
            var primary = Path.GetFullPath(Path.Combine(paths.Logs, FileName));
            using var mutex = CreateMutex(primary);
            if (!TryEnter(mutex))
                return files.Select(name => new DiagnosticLogSnapshotFile(name, null, "等待日志写入锁超时。")).ToArray();
            try
            {
                return files.Select(name =>
                {
                    try
                    {
                        var path = Path.Combine(paths.Logs, name);
                        return ReadSnapshotFile(path, name);
                    }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        return new DiagnosticLogSnapshotFile(name, null, failure.Message);
                    }
                }).ToArray();
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return files.Select(name => new DiagnosticLogSnapshotFile(name, null, failure.Message)).ToArray();
        }
    }

    private static DiagnosticLogSnapshotFile ReadSnapshotFile(string path, string name)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (FileNotFoundException) { return new DiagnosticLogSnapshotFile(name, null, null); }
        catch (DirectoryNotFoundException) { return new DiagnosticLogSnapshotFile(name, null, null); }
        using (stream)
        {
            if (stream.Length <= MaxSnapshotFileBytes)
            {
                var content = new byte[(int)stream.Length];
                stream.ReadExactly(content);
                return new DiagnosticLogSnapshotFile(name, content, null);
            }

            // Old installations may have oversized files beyond today's writer
            // cap. Preserve only the newest bounded tail and start at a line edge.
            stream.Seek(-MaxSnapshotFileBytes, SeekOrigin.End);
            var tail = new byte[MaxSnapshotFileBytes];
            stream.ReadExactly(tail);
            var firstLine = Array.IndexOf(tail, (byte)'\n');
            var start = firstLine >= 0 ? firstLine + 1 : FirstUtf8Boundary(tail);
            return new DiagnosticLogSnapshotFile(name, tail.AsSpan(start).ToArray(), null, Truncated: true);
        }
    }

    private static int FirstUtf8Boundary(byte[] bytes)
    {
        // When a legacy oversized event has no line break, the bounded tail
        // may begin in the middle of a multibyte UTF-8 character. Drop only
        // leading continuation bytes; the remainder begins at a code-point
        // boundary and retains almost the entire newest tail.
        var offset = 0;
        while (offset < bytes.Length && (bytes[offset] & 0xC0) == 0x80)
            offset++;
        return offset;
    }

    private static Mutex CreateMutex(string path)
    {
        // Windows paths are case-insensitive: canonicalize casing before hashing
        // so all processes addressing the same root choose the same mutex name.
        var normalized = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var name = $"WeChatBridge.DiagnosticLog.{digest}";
        return new Mutex(false, OperatingSystem.IsWindows() ? $"Local\\{name}" : name);
    }

    private static bool TryEnter(Mutex mutex)
    {
        try { return mutex.WaitOne(MutexWaitMilliseconds); }
        catch (AbandonedMutexException) { return true; }
    }

    private static string Format(LogLevel level, string message, Exception? error)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level.ToString().ToUpperInvariant()}] pid={Environment.ProcessId} {Sanitize(message)}";
        if (error is not null)
            line += " || " + Sanitize(error.ToString()); // Includes inner and aggregate causes.
        return TruncateUtf8(line, MaxEventBytes - 1); // The writer appends one LF byte.
    }

    private static string Sanitize(string? value) =>
        (value ?? "").Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\r", " ⏎ ");

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Utf8.GetByteCount(value) <= maxBytes) return value;
        const string marker = " …[truncated]";
        var budget = maxBytes - Utf8.GetByteCount(marker);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > budget) break;
            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return builder.Append(marker).ToString();
    }

    private static bool Roll(string file)
    {
        try
        {
            for (var generation = KeepGenerations; generation >= 1; generation--)
            {
                var source = generation == 1 ? file : $"{file}.{generation - 1}";
                var target = $"{file}.{generation}";
                if (!File.Exists(source)) continue;
                File.Move(source, target, overwrite: true);
            }
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
