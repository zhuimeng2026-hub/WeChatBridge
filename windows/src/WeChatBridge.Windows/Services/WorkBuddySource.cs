using System.IO;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// The health of the source folder. <see cref="Ready"/> means "readable and
/// watchable", never "WorkBuddy is installed" — this product watches a local
/// folder and has no connection to WorkBuddy to claim.
/// </summary>
public enum WorkBuddySourceState
{
    /// <summary>A probe is in flight; the UI shows "正在检查".</summary>
    Checking,
    /// <summary>The folder does not exist. Waited for, never created.</summary>
    Missing,
    /// <summary>The folder exists but cannot be enumerated.</summary>
    AccessDenied,
    /// <summary>Readable, but a watcher could not be established.</summary>
    WatchUnavailable,
    /// <summary>Readable and watchable; reception can start.</summary>
    Ready,
}

/// <summary>One detection result: where we looked, how, and what came back.</summary>
public sealed record WorkBuddySourceProbeResult(
    WorkBuddySourceState State,
    string ResolvedPath,
    bool IsOverride,
    DateTimeOffset CheckedAt,
    string? Detail)
{
    public static WorkBuddySourceProbeResult Checking(string path, bool isOverride) =>
        new(WorkBuddySourceState.Checking, path, isOverride, DateTimeOffset.Now, null);
}

/// <summary>
/// Turns the preference into a concrete folder. A user-chosen override wins only
/// when it is a rooted path; anything else falls back to the documented location
/// rather than silently pointing the archive at a relative folder.
/// </summary>
public static class WorkBuddySourceResolver
{
    /// <summary>
    /// WorkBuddy's current per-user export folder. This is a compatibility
    /// convention of this product, not a promise WorkBuddy makes about future
    /// versions — a change is handled by re-detection or a manual override.
    /// </summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy", "app", "tmp", "chat-history");

    public static (string Path, bool IsOverride) Resolve(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var trimmed = overridePath.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                try { return (Path.GetFullPath(trimmed), true); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A malformed override is not a reason to guess elsewhere.
                }
            }
        }
        return (DefaultDirectory, false);
    }
}

/// <summary>
/// Read-only detection. It never creates the source folder, writes a probe file,
/// or reads anything but top-level metadata — detection must not modify
/// WorkBuddy's data, and a missing folder must stay missing.
/// </summary>
public sealed class WorkBuddySourceProbe
{
    public WorkBuddySourceProbeResult Probe(string? overridePath)
    {
        var (path, isOverride) = WorkBuddySourceResolver.Resolve(overridePath);
        var checkedAt = DateTimeOffset.Now;
        if (!Directory.Exists(path))
            return new(WorkBuddySourceState.Missing, path, isOverride, checkedAt, null);
        try
        {
            // Enumerating the top level is the cheapest proof that the folder
            // answers with data rather than a policy violation. The enumerator
            // is lazy, so advancing once is what actually opens the handle.
            using var entries = Directory.EnumerateFiles(path, "*.zip", SearchOption.TopDirectoryOnly)
                .GetEnumerator();
            _ = entries.MoveNext();
            return new(WorkBuddySourceState.Ready, path, isOverride, checkedAt, null);
        }
        catch (UnauthorizedAccessException error)
        {
            return new(WorkBuddySourceState.AccessDenied, path, isOverride, checkedAt, error.Message);
        }
        catch (IOException error)
        {
            return new(WorkBuddySourceState.WatchUnavailable, path, isOverride, checkedAt, error.Message);
        }
    }
}

/// <summary>
/// Checks that the vault can actually be written to, using a short-lived probe
/// file that is always removed. "Can be read" is not "can be written": a vault on
/// a detached drive answers reads from the cache and fails the first save.
/// </summary>
public static class DestinationProbe
{
    public static (bool Ok, string? Detail) Check(string? vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return (false, null);
        if (!Directory.Exists(vaultPath))
            return (false, "知识库文件夹不存在，可能已被移动或所在磁盘未连接。");
        var probe = Path.Combine(vaultPath, ".lawdesk-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }
            return (true, null);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return (false, error.Message);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A leftover probe file is diagnostic noise, not a failed check.
            }
        }
    }
}
