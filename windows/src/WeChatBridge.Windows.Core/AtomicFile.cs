namespace WeChatBridge.Windows.Core;

/// <summary>
/// Writes a durable file by staging the bytes next to it and moving them into
/// place.
/// <para>
/// Every store in this repo that has to survive a crash writes this way
/// (<c>ConfigStore.Save</c>, <c>BatchCollectionLedger.Save</c>,
/// <c>ForwardTargetStore</c>, <c>KnowledgeDelivery.WriteAtomic</c>,
/// <c>LicenseStore</c>, <c>InstallIdentityStore</c>). A bare
/// <see cref="File.WriteAllText(string, string?)"/> truncates the live file
/// first and writes afterwards, so a crash, a full disk or an AV scan in
/// between leaves half a file where a valid one used to be.
/// </para>
/// <para>
/// For <c>state.json</c> that is not a cosmetic problem: a truncated state
/// file makes the batch look never-processed, so the share is announced again
/// and — its <c>intent.json</c> having been consumed already — recorded as
/// expired instead of the delivery that actually happened. For
/// <c>manifest.json</c> it makes the whole batch unreadable, and an unreadable
/// batch is pruned.
/// </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes UTF-8 text to <paramref name="path"/> so that the live file
    /// either holds its previous contents or the new ones — never a mixture.
    /// </summary>
    /// <exception cref="IOException">
    /// The staged bytes could not be written or moved into place. The live file
    /// still holds whatever it held before the call.
    /// </exception>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        // The staging file is a sibling of the target, so the move is a rename
        // inside one directory: it cannot half-succeed the way a copy can.
        var staging = path + ".tmp";
        File.WriteAllText(staging, contents);
        try
        {
            File.Move(staging, path, overwrite: true);
        }
        catch
        {
            // The staged bytes are worthless once the move failed, and the live
            // file still holds the contents it had before this call.
            try { File.Delete(staging); } catch { }
            throw;
        }
    }
}
