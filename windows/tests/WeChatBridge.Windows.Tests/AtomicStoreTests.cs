using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The live files of a batch are written the way every other durable store in
/// this repo writes: the bytes are staged next to the target and moved into
/// place. A bare <c>File.WriteAllText</c> truncates the live file first and
/// writes afterwards, so a crash, a full disk or an AV scan in between leaves
/// half a file where a valid one used to be.
/// <para>
/// For <c>state.json</c> that is not cosmetic: a truncated state file makes
/// the batch look never-processed, so the share is announced a second time and
/// — its <c>intent.json</c> having been consumed already — recorded as expired
/// instead of the delivery that actually happened. For <c>manifest.json</c> it
/// makes the whole batch unreadable, and an unreadable batch is pruned.
/// </para>
/// </summary>
public sealed class AtomicStoreTests
{
    [Fact]
    public async Task AFailedStateWriteLeavesThePreviousStateOnDisk()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var committed = await CommitAsync(fixture, "one.zip");
        reader.RecordOutcome(
            new BatchOutcome(BatchOutcomeKind.Delivered, "已送达", DateTimeOffset.UtcNow),
            committed.BatchId, "Codex");

        var statePath = Path.Combine(committed.BatchDirectory, BatchState.FileName);
        var before = File.ReadAllText(statePath);
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(committed.BatchId)?.Outcome?.Kind);

        // The staging path is taken by a directory, so the write cannot start
        // at all — the closest a test can get to a crash mid-write.
        Directory.CreateDirectory(statePath + ".tmp");
        Assert.ThrowsAny<Exception>(() => reader.RecordOutcome(
            new BatchOutcome(BatchOutcomeKind.Failed, "失败", DateTimeOffset.UtcNow),
            committed.BatchId, "Codex"));

        // The live file still holds the previous bytes, and still reads back as
        // the delivery that happened.
        Assert.Equal(before, File.ReadAllText(statePath));
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(committed.BatchId)?.Outcome?.Kind);
    }

    [Fact]
    public async Task AFailedManifestRewriteLeavesTheBatchReadable()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var committed = await CommitAsync(fixture, "one.zip", "two.zip");
        var batch = reader.LoadBatches().Single();
        var manifestPath = Path.Combine(committed.BatchDirectory, "manifest.json");
        var before = File.ReadAllText(manifestPath);

        Directory.CreateDirectory(manifestPath + ".tmp");
        Assert.ThrowsAny<Exception>(() => reader.Discard(batch.Items[0]));

        // The manifest was not truncated: it still parses and still names the
        // files it named before.
        Assert.Equal(before, File.ReadAllText(manifestPath));
        var manifest = JsonSerializer.Deserialize<BatchManifest>(before, BatchManifest.JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Items.Count);
        // And the batch is still a batch — a truncated manifest would have made
        // it unreadable, and an unreadable batch is pruned.
        Assert.Single(reader.LoadBatches());
    }

    [Fact]
    public async Task ASuccessfulWriteLeavesNoStagingFileBehind()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var committed = await CommitAsync(fixture, "one.zip");
        reader.RecordOutcome(
            new BatchOutcome(BatchOutcomeKind.Delivered, "已送达", DateTimeOffset.UtcNow),
            committed.BatchId, "Codex");

        var statePath = Path.Combine(committed.BatchDirectory, BatchState.FileName);
        Assert.False(File.Exists(statePath + ".tmp"));
        Assert.False(Directory.Exists(statePath + ".tmp"));
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(committed.BatchId)?.Outcome?.Kind);
    }

    [Fact]
    public void TheLedgerAndTheConfigAreWrittenTheSameWay()
    {
        using var fixture = new TempInbox();
        var ledger = new BatchCollectionLedger();
        ledger.Append(Guid.NewGuid());
        var path = Path.Combine(fixture.Root, "collections.json");
        ledger.Save(path);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(ledger.Collections.Count, BatchCollectionLedger.Load(path).Collections.Count);

        ConfigStore.Save(fixture.Root, "forward-targets.json", new List<ForwardTarget>());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "forward-targets.json.tmp")));
    }

    private static async Task<BatchCommitResult> CommitAsync(TempInbox fixture, params string[] names)
    {
        var sources = names
            .Select(name => new InboxSourceFile(fixture.WriteSource(name, "bytes"), name, "application/zip", 0, 0))
            .ToList();
        return await InboxWriter.CommitAsync(fixture.Paths, sources);
    }
}
