using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>How serious a log line is.</summary>
public enum LogLevel
{
    /// <summary>Normal progress. Something happened; nothing is wrong.</summary>
    Info,

    /// <summary>
    /// Something recoverable went wrong — a step was skipped, a fallback was
    /// taken. The user may not notice, but a support question may hinge on it.
    /// </summary>
    Warn,

    /// <summary>A feature did not do what it was asked to do.</summary>
    Error,
}

/// <summary>
/// The diagnostic log: the record of what the product was doing when something
/// went wrong.
/// <para>
/// This exists because the product runs on machines nobody has seen. A user
/// reports "it did not work"; the only way to find out why is a log that says
/// what was attempted, what the environment looked like, and what came back.
/// Without it, every question becomes a guess.
/// </para>
/// <para>
/// Three properties matter more than richness:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>It never breaks the product.</b> A log that cannot be written must not
/// take the feature down with it. Writes are best-effort and failures are
/// swallowed here, not propagated.
/// </item>
/// <item>
/// <b>It never grows without bound.</b> The log rolls over, so a machine that
/// runs for months keeps its recent history and does not fill the disk.
/// </item>
/// <item>
/// <b>It is readable without tools.</b> One line per event, timestamped, with
/// a level marker — so the first thing to try is opening the file in
/// Notepad.
/// </item>
/// </list>
/// </summary>
public static class DiagnosticLog
{
    /// <summary>The file the log is written to, inside the inbox's Logs folder.</summary>
    public const string FileName = "windows.log";

    /// <summary>How large the log may grow before it rolls over.</summary>
    private const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>How many rolled-over copies to keep.</summary>
    private const int KeepGenerations = 3;

    private static readonly object Gate = new();

    /// <summary>
    /// Records an event. The exception is written in full — type, message and
    /// stack trace — because "something went wrong" without a stack is rarely
    /// enough to locate the problem.
    /// </summary>
    public static void Write(InboxPaths paths, LogLevel level, string message, Exception? error = null)
    {
        try
        {
            paths.EnsureCreated();
            var line = Format(level, message, error);
            lock (Gate)
            {
                var file = Path.Combine(paths.Logs, FileName);
                Roll(file);
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must not break the feature that logged. There is nowhere
            // left to report this to, and a second failure inside the failure
            // handler is worse than the missing line.
        }
    }

    /// <summary>Records a normal-progress event.</summary>
    public static void Info(InboxPaths paths, string message) => Write(paths, LogLevel.Info, message);

    /// <summary>Records a normal-progress event with a cause attached.</summary>
    public static void Info(InboxPaths paths, string message, Exception? error) =>
        Write(paths, LogLevel.Info, message, error);

    /// <summary>Records a recoverable problem.</summary>
    public static void Warn(InboxPaths paths, string message, Exception? error = null) =>
        Write(paths, LogLevel.Warn, message, error);

    /// <summary>Records a failure.</summary>
    public static void Error(InboxPaths paths, string message, Exception? error = null) =>
        Write(paths, LogLevel.Error, message, error);

    /// <summary>
    /// One line per event. The stack trace is folded onto the same line so
    /// that a line number in the file still identifies one event — a
    /// multi-line stack would otherwise break every tool that reads the file
    /// line by line, including <c>grep</c> and Notepad.
    /// </summary>
    private static string Format(LogLevel level, string message, Exception? error)
    {
        var builder = new StringBuilder();
        // ISO 8601 with an offset, as the previous format used: it sorts
        // correctly and stays unambiguous across daylight-saving changes.
        builder.Append(DateTimeOffset.UtcNow.ToString("O"));
        builder.Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']');
        builder.Append(' ').Append(Sanitize(message));
        if (error is not null)
        {
            builder.Append(" || ").Append(Sanitize(error.GetType().FullName ?? error.GetType().Name));
            builder.Append(": ").Append(Sanitize(error.Message));
            var stack = error.StackTrace;
            if (!string.IsNullOrWhiteSpace(stack))
            {
                builder.Append(" || stack=").Append(Sanitize(stack));
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Flattens a value onto one line: newlines would break the one-line-per-
    /// event rule, and carriage returns would corrupt the file in Notepad.
    /// </summary>
    private static string Sanitize(string? value) =>
        (value ?? "")
        .Replace("\r\n", " ⏎ ")
        .Replace("\n", " ⏎ ")
        .Replace("\r", " ⏎ ");

    /// <summary>
    /// Rolls the log over when it grows past the limit. The previous
    /// generations are shifted along, so the newest history is always the one
    /// kept.
    /// </summary>
    private static void Roll(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            // Delete the oldest generation first, then shift the rest down.
            var oldest = $"{file}.{KeepGenerations}";
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (var generation = KeepGenerations - 1; generation >= 1; generation--)
            {
                var from = $"{file}.{generation}";
                if (!File.Exists(from))
                    continue;
                var to = $"{file}.{generation + 1}";
                File.Move(from, to, overwrite: true);
            }
            File.Move(file, $"{file}.1", overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // A log that cannot be rolled is still a usable log. Keep writing
            // to it rather than losing the event entirely.
        }
    }
}
