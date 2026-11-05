using Microsoft.Win32;
using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Points the <c>obsidian://</c> protocol at the copy of Obsidian on the drive.
/// <para>
/// The handler is registered by Obsidian's <em>installer</em>, and a portable
/// build has none — so <see cref="MainViewModel.OpenInObsidian"/>'s URI would
/// go nowhere. Registering it ourselves is three HKCU values pointing at the
/// same executable the installer would have used, which is why
/// <c>Obsidian.exe "obsidian://open?…"</c> is the one invocation that always
/// works: the shell passes the URI straight through as argv[1]. (Obsidian does
/// not accept <c>Obsidian.exe "&lt;vault&gt;" "&lt;note&gt;"</c> — positional
/// arguments are a long-standing feature request, never implemented — so there
/// is no alternative to the URI.)
/// </para>
/// <para>
/// Only writes when nothing owns the protocol, so an installed Obsidian that
/// already registered itself is never redirected.
/// </para>
/// </summary>
internal static class ObsidianProtocol
{
    private const string ProtocolKeyPath = @"Software\Classes\obsidian";
    private const string CommandKeyPath = @"Software\Classes\obsidian\shell\open\command";

    /// <summary>The Obsidian shipped on the portable drive, or null.</summary>
    public static string? PortableExecutable
    {
        get
        {
            var root = AppLayout.Current.PortableRoot;
            if (root is null)
                return null;
            var executable = Path.Combine(root, "Obsidian", "Obsidian.exe");
            return File.Exists(executable) ? executable : null;
        }
    }

    /// <summary>
    /// The executable the protocol currently points at, or null when nothing
    /// claims it. The path is parsed out of the command value and is returned
    /// even when the file no longer exists — callers that need "will this
    /// actually work" must ask <see cref="IsRegistered"/>.
    /// </summary>
    public static string? RegisteredExecutable
    {
        get
        {
            var command = RegisteredCommand;
            if (string.IsNullOrWhiteSpace(command))
                return null;
            // The shell convention is "C:\path\Obsidian.exe" "%1". A command
            // without quotes is ambiguous — the path itself may contain spaces
            // — so the longest prefix that exists on disk wins, and an
            // unparseable value answers null rather than a guess.
            var text = command.Trim();
            if (text.StartsWith('"'))
            {
                var end = text.IndexOf('"', 1);
                return end > 1 ? text[1..end] : null;
            }
            var candidate = text;
            while (candidate.Length > 0)
            {
                if (File.Exists(candidate))
                    return candidate;
                var cut = candidate.LastIndexOf(' ');
                if (cut <= 0)
                    break;
                candidate = candidate[..cut];
            }
            return null;
        }
    }

    /// <summary>The raw command value behind the protocol, or null when unclaimed.</summary>
    public static string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath);
                return key?.GetValue(null) as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// True when an Obsidian that can actually answer the URI owns the
    /// protocol.
    /// <para>
    /// The registry entry alone is not enough. Uninstalling or moving Obsidian
    /// leaves the key behind pointing at a file that is gone; treating that as
    /// "registered" makes the caller believe the URI will open, so it never
    /// falls back to the file manager and the user just sees nothing happen.
    /// </para>
    /// </summary>
    public static bool IsRegistered()
    {
        var executable = RegisteredExecutable;
        return executable is not null && File.Exists(executable);
    }

    /// <summary>
    /// Registers the protocol when it is free. Returns true when the URI will
    /// resolve afterwards.
    /// </summary>
    public static bool Ensure()
    {
        var executable = PortableExecutable;
        if (executable is null || IsRegistered())
            return IsRegistered();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath, writable: true);
            key?.SetValue(null, "URL:obsidian");
            key?.SetValue("URL Protocol", "");
            using var command = Registry.CurrentUser.CreateSubKey(CommandKeyPath, writable: true);
            command?.SetValue(null, $"\"{executable}\" \"%1\"");
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
            return false;
        }
    }

    /// <summary>Removes the registration, but only if it is ours.</summary>
    public static void RemoveIfOurs()
    {
        var executable = PortableExecutable;
        if (executable is null)
            return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath);
            if (key?.GetValue(null) is not string command
                || !command.Contains(executable, StringComparison.OrdinalIgnoreCase))
                return;
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKeyPath);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }
}
