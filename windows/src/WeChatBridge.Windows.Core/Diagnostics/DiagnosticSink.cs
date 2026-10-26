using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// The one place the application writes diagnostics to.
/// <para>
/// Most of the code that needs to log is a UI element or a helper that was
/// never given the inbox paths, and threading a constructor parameter
/// through all of them would change signatures for no benefit. The paths are
/// known once, at startup, so they are set once here instead.
/// </para>
/// <para>
/// A null sink means "logging is not available" — the tests, and the window
/// between process start and the first successful path resolution. Calls are
/// no-ops then, never failures.
/// </para>
/// </summary>
public static class DiagnosticSink
{
    private static InboxPaths? _paths;

    /// <summary>The paths to write to, once they are known.</summary>
    public static void Attach(InboxPaths paths)
    {
        _paths = paths;
    }

    /// <summary>Whether logging is currently possible.</summary>
    public static bool Available => _paths is not null;

    /// <summary>Records a normal-progress event.</summary>
    public static void Info(string message) => Write(LogLevel.Info, message, null);

    /// <summary>Records a normal-progress event with a cause attached.</summary>
    public static void Info(string message, Exception? error) => Write(LogLevel.Info, message, error);

    /// <summary>Records a recoverable problem.</summary>
    public static void Warn(string message, Exception? error = null) => Write(LogLevel.Warn, message, error);

    /// <summary>Records a failure.</summary>
    public static void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error);

    /// <summary>Records an event at an explicit level.</summary>
    public static void Write(LogLevel level, string message, Exception? error)
    {
        var paths = _paths;
        if (paths is null)
            return;
        DiagnosticLog.Write(paths, level, message, error);
    }
}
