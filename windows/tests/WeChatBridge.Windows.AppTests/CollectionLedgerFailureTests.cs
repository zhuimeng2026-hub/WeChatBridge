using System.IO;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;
using WeChatBridge.Windows.Tests;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// A corrupt collection ledger must not take the app with it.
/// <para>
/// The ledger used to be loaded before the history was published and threw
/// when it could not be parsed: every launch from then on showed an empty
/// history, and right-clicking the tray icon threw inside the window
/// procedure, where an exception escapes into the WPF message loop. There was
/// no way back — the file stayed on disk, unreadable, and the app kept dying
/// on it.
/// </para>
/// </summary>
public sealed class CollectionLedgerFailureTests
{
    /// <summary>A ledger whose schema this build does not know.</summary>
    private const string Unreadable = "{\"schemaVersion\":8,\"collections\":[],\"seenBatchIDs\":[]}";

    [Fact]
    public void ACorruptLedgerStillLetsTheAppLoadItsHistory()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var originalLanguage = L10n.Language;
            try
            {
                using var fixture = new TempInbox();
                var corrupt = Path.Combine(fixture.Root, "collections.json");
                File.WriteAllText(corrupt, Unreadable);
                var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
                var config = Path.Combine(fixture.Root, "config");
                using var scenes = new SceneService(config);

                var source = fixture.WriteSource("sample.zip", "bytes");
                var batch = InboxWriter.CommitAsync(fixture.Paths,
                    [new InboxSourceFile(source, "sample.zip", "application/zip", 0, 0)]).GetAwaiter().GetResult();
                reader.RecordChatName(batch.BatchId, "示例群聊");

                var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
                var toasts = new List<string>();
                model.ToastRequested += (message, _, _, _) => toasts.Add(message);
                model.Reload();

                // The history is published even though the ledger is broken.
                Assert.Single(model.Batches);
                Assert.Null(model.InboxFailure);

                // The problem is said out loud — once, not on every reload.
                Assert.Contains(toasts, toast => toast.Contains("收集记录", StringComparison.Ordinal));

                // The minimal tray menu is three fixed commands and no longer
                // reads the ledger, so a corrupt ledger cannot make right-click
                // throw inside the window procedure either.
                Assert.Null(model.Collections.Ledger.Current);
                Assert.NotEmpty(TrayMenu.Build(windowVisible: false, paused: false));

                // The unreadable file is still there, byte for byte.
                Assert.Equal(Unreadable, File.ReadAllText(corrupt));
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                L10n.Language = originalLanguage;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}
