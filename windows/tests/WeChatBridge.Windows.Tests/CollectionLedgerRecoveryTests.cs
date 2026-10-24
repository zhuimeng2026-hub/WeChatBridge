using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A ledger that cannot be parsed is the user's file to look at, not a reason
/// for the app not to start.
/// <para>
/// It used to be the opposite: <c>BatchCollectionLedger.Load</c> threw, the
/// service constructor threw with it, and every launch from then on showed an
/// empty history — while the file stayed on disk, unreadable and unexplained.
/// The bytes are the user's; they stay exactly where they are.
/// </para>
/// </summary>
public sealed class CollectionLedgerRecoveryTests
{
    /// <summary>A ledger whose schema this build does not know.</summary>
    private const string Unreadable = "{\"schemaVersion\":8,\"collections\":[],\"seenBatchIDs\":[]}";

    [Fact]
    public void AnUnreadableLedgerDoesNotStopTheServiceAndKeepsTheOriginalBytes()
    {
        using var fixture = new TempInbox();
        var path = Path.Combine(fixture.Root, CollectionService.LedgerFileName);
        File.WriteAllText(path, Unreadable);

        var service = new CollectionService(fixture.Paths, new InboxReader(fixture.Paths));

        Assert.NotNull(service.LoadFailure);
        Assert.Empty(service.Ledger.Collections);
        // The user's bytes are still there, byte for byte.
        Assert.Equal(Unreadable, File.ReadAllText(path));
    }

    [Fact]
    public void ATruncatedLedgerIsTreatedTheSameWay()
    {
        using var fixture = new TempInbox();
        var path = Path.Combine(fixture.Root, CollectionService.LedgerFileName);
        File.WriteAllText(path, "{\"schemaVersion\":1,\"collections\":[{\"id\"");

        var service = new CollectionService(fixture.Paths, new InboxReader(fixture.Paths));

        Assert.NotNull(service.LoadFailure);
        Assert.Empty(service.Ledger.Collections);
        Assert.Equal("{\"schemaVersion\":1,\"collections\":[{\"id\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task CollectingKeepsWorkingAfterAnUnreadableLedgerAndSurvivesARestart()
    {
        using var fixture = new TempInbox();
        var path = Path.Combine(fixture.Root, CollectionService.LedgerFileName);
        File.WriteAllText(path, Unreadable);
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var batch = await AddBatch(fixture, reader);

        var group = service.Append(batch, "甲群");

        // A restart keeps the collection...
        var restarted = new CollectionService(fixture.Paths, reader);
        Assert.Equal([batch.Id], restarted.Ledger.Editable(group).BatchIDs);
        // ...and the unreadable file is still exactly as it was.
        Assert.Equal(Unreadable, File.ReadAllText(path));
    }

    [Fact]
    public async Task TheRecoveredLedgerIsStillThereAfterTheUnreadableFileIsRemoved()
    {
        using var fixture = new TempInbox();
        var path = Path.Combine(fixture.Root, CollectionService.LedgerFileName);
        File.WriteAllText(path, Unreadable);
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var batch = await AddBatch(fixture, reader);
        var group = service.Append(batch, "甲群");
        service.Ledger.Rename(group, "甲群");
        service.Save();

        // The user removes the file they could not read.
        File.Delete(path);

        var restarted = new CollectionService(fixture.Paths, reader);
        Assert.Single(restarted.Ledger.Collections);
        Assert.Equal("甲群", restarted.Ledger.Collections[0].Name);
        Assert.Equal([batch.Id], restarted.Ledger.Editable(group).BatchIDs);
    }

    [Fact]
    public void AHealthyLedgerIsUsedAsIs()
    {
        using var fixture = new TempInbox();
        var path = Path.Combine(fixture.Root, CollectionService.LedgerFileName);
        var ledger = new BatchCollectionLedger();
        ledger.Append(Guid.NewGuid());
        ledger.Save(path);

        var service = new CollectionService(fixture.Paths, new InboxReader(fixture.Paths));

        Assert.Null(service.LoadFailure);
        Assert.Single(service.Ledger.Collections);
        Assert.False(File.Exists(Path.Combine(fixture.Root, CollectionService.RecoveredLedgerFileName)));
    }

    private static async Task<ReadyBatch> AddBatch(TempInbox fixture, InboxReader reader)
    {
        var source = fixture.WriteSource("聊天记录.zip", "original-archive-bytes");
        var committed = await InboxWriter.CommitAsync(fixture.Paths,
            [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)], action: ShareAction.Collect);
        return reader.LoadBatches().Single(b => b.Id == committed.BatchId);
    }
}
