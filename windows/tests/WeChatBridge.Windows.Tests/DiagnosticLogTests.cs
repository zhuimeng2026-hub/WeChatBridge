using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Tests for the diagnostic log.
/// <para>
/// The log is the only evidence available when something goes wrong on a
/// machine nobody has seen, so the properties that make it usable have to be
/// guaranteed: one event per line, the level visible, the stack present, and
/// a bound on how much disk it may take.
/// </para>
/// </summary>
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatBridgeLogTests",
        Guid.NewGuid().ToString("N"));
    private readonly InboxPaths _paths;

    public DiagnosticLogTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new InboxPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string LogFile => Path.Combine(_paths.Logs, DiagnosticLog.FileName);

    private string[] Lines() =>
        File.Exists(LogFile)
            ? File.ReadAllLines(LogFile)
            : [];

    [Fact]
    public void A_line_is_written_to_the_log()
    {
        DiagnosticLog.Info(_paths, "启动完成。");

        Assert.Single(Lines());
        Assert.Contains("启动完成。", Lines()[0]);
    }

    [Fact]
    public void The_level_is_visible_on_the_line()
    {
        DiagnosticLog.Info(_paths, "普通事件。");
        DiagnosticLog.Warn(_paths, "可恢复的问题。");
        DiagnosticLog.Error(_paths, "失败。");

        var lines = Lines();
        Assert.Equal(3, lines.Length);
        Assert.Contains("[INFO]", lines[0]);
        Assert.Contains("[WARN]", lines[1]);
        Assert.Contains("[ERROR]", lines[2]);
    }

    [Fact]
    public void An_exception_is_recorded_with_its_type_message_and_stack()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("具体原因");
        }
        catch (Exception error)
        {
            captured = error;
        }

        DiagnosticLog.Error(_paths, "操作失败。", captured);

        var line = Lines()[0];
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("具体原因", line);
        // The stack is what locates the problem; without it the line only
        // says what went wrong, not where.
        Assert.Contains("stack=", line);
        Assert.Contains("DiagnosticLogTests", line);
    }

    [Fact]
    public void One_event_stays_on_one_line()
    {
        // A multi-line stack would break every line-by-line reader, and the
        // first thing anyone does with a log is read it line by line.
        Exception captured;
        try
        {
            throw new InvalidOperationException("第一行\n第二行");
        }
        catch (Exception error)
        {
            captured = error;
        }

        DiagnosticLog.Error(_paths, "多行消息\n也在一行里。", captured);

        var lines = Lines();
        Assert.Single(lines);
        Assert.DoesNotContain("\n", lines[0]);
    }

    [Fact]
    public void The_timestamp_comes_first_and_sorts()
    {
        DiagnosticLog.Info(_paths, "第一条。");
        DiagnosticLog.Info(_paths, "第二条。");

        var lines = Lines();
        Assert.True(string.CompareOrdinal(lines[0], lines[1]) < 0);
    }

    [Fact]
    public void The_log_rolls_over_instead_of_growing_without_bound()
    {
        // A machine that runs for months must not fill its disk with logs.
        var filler = new string('x', 64 * 1024);
        for (var i = 0; i < 80; i++)
            DiagnosticLog.Info(_paths, filler);

        // The current file is bounded, and the older generations were kept.
        var current = new FileInfo(LogFile);
        Assert.True(current.Length < 8 * 1024 * 1024, $"日志未轮转，当前大小 {current.Length}");
        Assert.True(File.Exists(LogFile + ".1"));
    }

    [Fact]
    public void The_rolled_over_generations_are_kept_in_order()
    {
        var filler = new string('y', 64 * 1024);
        for (var i = 0; i < 200; i++)
            DiagnosticLog.Info(_paths, filler);

        Assert.True(File.Exists(LogFile + ".1"));
        // The most recent generation is .1, not .3.
        var newest = new FileInfo(LogFile + ".1");
        var oldest = File.Exists(LogFile + ".3") ? new FileInfo(LogFile + ".3") : null;
        if (oldest is not null)
            Assert.True(newest.LastWriteTimeUtc >= oldest.LastWriteTimeUtc);
    }

    [Fact]
    public void Writing_never_throws_even_when_the_log_cannot_be_written()
    {
        // A log that takes the feature down with it is worse than no log.
        var broken = new InboxPaths(Path.Combine(_root, "no-such-root", "\0invalid"));

        // No exception is the assertion.
        DiagnosticLog.Error(broken, "写入会失败。", new Exception("原因"));
    }

    [Fact]
    public void The_legacy_entry_point_still_works()
    {
        // Fifty-odd call sites use the old signature; they must keep working
        // and must now carry a level.
        InboxLogger.Write(_paths, "旧接口：没有异常。");
        InboxLogger.Write(_paths, "旧接口：有异常。", new InvalidOperationException("原因"));

        var lines = Lines();
        Assert.Equal(2, lines.Length);
        Assert.Contains("[INFO]", lines[0]);
        Assert.Contains("[ERROR]", lines[1]);
        Assert.Contains("InvalidOperationException", lines[1]);
    }

    [Fact]
    public void The_log_file_uses_utf8_so_chinese_survives()
    {
        DiagnosticLog.Info(_paths, "中文路径 D:\\律档通\\律档通\\Vault");

        var text = File.ReadAllText(LogFile, new System.Text.UTF8Encoding(false));
        Assert.Contains("律档通", text);
    }
}
