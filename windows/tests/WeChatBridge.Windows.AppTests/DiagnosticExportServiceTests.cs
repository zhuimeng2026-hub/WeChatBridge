using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;
using WeChatBridge.Windows.Tests;

namespace WeChatBridge.Windows.AppTests;

public sealed class DiagnosticExportServiceTests
{
    [Fact]
    public async Task Independent_processes_share_the_path_mutex_without_losing_events()
    {
        using var inbox = new TempInbox();
        const int processCount = 6;
        const int eventsPerProcess = 1200;
        var probe = Path.Combine(AppContext.BaseDirectory, "diagnostic-log-probe", "DiagnosticLogProcessProbe.dll");
        Assert.True(File.Exists(probe), $"Missing process probe: {probe}");

        var processes = new List<Process>();
        try
        {
            for (var id = 0; id < processCount; id++)
            {
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                start.ArgumentList.Add(probe);
                start.ArgumentList.Add(inbox.Root);
                start.ArgumentList.Add(id.ToString());
                start.ArgumentList.Add(eventsPerProcess.ToString());
                var child = Process.Start(start)!;
                processes.Add(child);
            }

            var readyUntil = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < readyUntil && Enumerable.Range(0, processCount)
                .Any(id => !File.Exists(Path.Combine(inbox.Root, $"ready-{id}"))))
                await Task.Delay(20);
            Assert.All(Enumerable.Range(0, processCount), id =>
                Assert.True(File.Exists(Path.Combine(inbox.Root, $"ready-{id}")), $"Process {id} did not reach barrier."));
            File.WriteAllText(Path.Combine(inbox.Root, "start"), "go");

            foreach (var child in processes)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                var stderr = await child.StandardError.ReadToEndAsync();
                Assert.True(child.ExitCode == 0, $"Probe failed ({child.ExitCode}): {stderr}");
            }

            var snapshot = DiagnosticLog.CreateSnapshot(inbox.Paths);
            var lines = snapshot.Where(file => file.Content is not null)
                .SelectMany(file => Encoding.UTF8.GetString(file.Content!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                .ToArray();
            Assert.Equal(processCount * eventsPerProcess, lines.Length);
            Assert.True(File.Exists(Path.Combine(inbox.Paths.Logs, "windows.log.1")), "Concurrent child processes must exercise rollover.");
            var ids = lines.SelectMany(line => System.Text.RegularExpressions.Regex.Matches(line, @"process-\d{2}-event-\d{4}")
                .Select(match => match.Value)).ToHashSet();
            Assert.Equal(processCount * eventsPerProcess, ids.Count);
            for (var id = 0; id < processCount; id++)
                for (var i = 0; i < eventsPerProcess; i++)
                    Assert.Contains($"process-{id:D2}-event-{i:D4}", ids);
        }
        finally
        {
            foreach (var child in processes)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                child.Dispose();
            }
        }
    }

    [Fact]
    public void Export_contains_only_the_log_allowlist_and_explains_missing_files()
    {
        using var inbox = new TempInbox();
        Directory.CreateDirectory(inbox.Paths.Logs);
        File.WriteAllText(Path.Combine(inbox.Paths.Logs, "windows.log"), "known-log");
        File.WriteAllText(Path.Combine(inbox.Paths.Logs, "windows.log.backup"), "not-allowlisted");
        File.WriteAllText(Path.Combine(inbox.Paths.Logs, "other.log"), "not-a-log");
        File.WriteAllText(Path.Combine(inbox.Paths.Root, "account.json"), "private-data");

        var bundle = DiagnosticExportService.Export(inbox.Paths, null, out var error);

        Assert.Null(error);
        Assert.NotNull(bundle);
        using var zip = ZipFile.OpenRead(bundle!);
        Assert.Contains(zip.Entries, entry => entry.FullName == "windows.log");
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("backup"));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName == "other.log");
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("account"));
        var missing = Read(zip, "日志读取说明/windows.log.1.txt");
        Assert.Contains("未找到", missing);
        var summary = Read(zip, "环境摘要.txt");
        Assert.Contains("路径", summary);
        Assert.Contains("异常上下文", summary);
        Assert.DoesNotContain("不包含", summary);
    }

    [Fact]
    public void Repeated_exports_use_distinct_paths_and_keep_both_archives_valid()
    {
        using var inbox = new TempInbox();
        DiagnosticLog.Info(inbox.Paths, "repeated-export");
        var first = DiagnosticExportService.Export(inbox.Paths, null, out var firstError);
        var second = DiagnosticExportService.Export(inbox.Paths, null, out var secondError);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        using var firstZip = ZipFile.OpenRead(first!);
        using var secondZip = ZipFile.OpenRead(second!);
        Assert.Contains(firstZip.Entries, entry => entry.FullName == "windows.log");
        Assert.Contains(secondZip.Entries, entry => entry.FullName == "windows.log");
    }

    [Fact]
    public void Unreadable_log_is_reported_without_creating_an_empty_log_entry()
    {
        using var inbox = new TempInbox();
        DiagnosticLog.Info(inbox.Paths, "readable-primary");
        var lockedPath = Path.Combine(inbox.Paths.Logs, "windows.log.1");
        File.WriteAllText(lockedPath, "locked-generation");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var bundle = DiagnosticExportService.Export(inbox.Paths, null, out var error);

        Assert.Null(error);
        using var zip = ZipFile.OpenRead(bundle!);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName == "windows.log.1");
        Assert.Contains(zip.Entries, entry => entry.FullName == "日志读取说明/windows.log.1.txt");
        Assert.Contains("未能读取", Read(zip, "日志读取说明/windows.log.1.txt"));
    }

    [Fact]
    public async Task Concurrent_writer_and_export_produce_valid_complete_log_snapshots()
    {
        using var inbox = new TempInbox();
        const int count = 800;
        DiagnosticLog.Info(inbox.Paths, "concurrent-0000");
        var writer = Task.Run(() =>
        {
            for (var i = 1; i < count; i++)
            {
                DiagnosticLog.Info(inbox.Paths, $"concurrent-{i:D4}");
                Thread.Sleep(1);
            }
        });
        var exports = new List<string>();
        while (!writer.IsCompleted)
        {
            var bundle = DiagnosticExportService.Export(inbox.Paths, null, out var error);
            Assert.Null(error);
            Assert.NotNull(bundle);
            exports.Add(bundle!);
        }
        await writer;
        if (exports.Count == 0)
        {
            var bundle = DiagnosticExportService.Export(inbox.Paths, null, out var error);
            Assert.Null(error);
            exports.Add(bundle!);
        }

        // Every exported archive is structurally complete and its snapshot is
        // a prefix of the serialized write stream, with no partial event lines.
        foreach (var bundle in exports)
        {
            using var zip = ZipFile.OpenRead(bundle);
            var log = zip.GetEntry("windows.log");
            Assert.NotNull(log);
            using var reader = new StreamReader(log!.Open(), Encoding.UTF8);
            foreach (var line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Assert.Matches(@"^\d{4}-\d\d-\d\dT.*\[INFO\] pid=\d+ concurrent-\d{4}$", line.TrimEnd('\r'));
        }

        // The final on-disk log set retains all events well within its capacity.
        var snapshot = DiagnosticLog.CreateSnapshot(inbox.Paths);
        var retained = snapshot.Where(item => item.Content is not null)
            .SelectMany(item => Encoding.UTF8.GetString(item.Content!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        Assert.Equal(count, retained.Length);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
