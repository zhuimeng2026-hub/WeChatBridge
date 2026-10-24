using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The share helper's wedge recovery sweeps "the other copies of me" so that
/// the pipe name frees for the process that takes over. A portable copy and an
/// installed copy run the very same helper under the very same file name, and
/// they deliberately keep separate pipes, mutexes and event names — so the
/// sweep has to compare the installation and not the process name.
/// <para>
/// It used to compare the process name alone: a portable share that took the
/// wedge path killed every <c>WeChatBridge.ShareTarget</c> on the machine,
/// including the installed build's resident keeper — and the installed
/// build's share relay died with it.
/// </para>
/// </summary>
public sealed class InstallationScopeTests
{
    [Fact]
    public void TwoPortableCopiesAreDifferentInstallations()
    {
        using var fixture = new TempInbox();
        var first = PortableStick(fixture, "stick-a");
        var second = PortableStick(fixture, "stick-b");

        Assert.NotEqual(
            AppLayout.SuffixForExecutable(HelperOf(first)),
            AppLayout.SuffixForExecutable(HelperOf(second)));
        Assert.False(AppLayout.IsSameInstallation(HelperOf(first), HelperOf(second)));
    }

    [Fact]
    public void APortableCopyAndTheInstalledBuildAreDifferentInstallations()
    {
        using var fixture = new TempInbox();
        var stick = PortableStick(fixture, "stick-a");
        var installed = InstalledCopy(fixture, "installed");

        Assert.False(AppLayout.IsSameInstallation(HelperOf(stick), HelperOf(installed)));
        Assert.False(AppLayout.IsSameInstallation(HelperOf(installed), HelperOf(stick)));
    }

    [Fact]
    public void TheHelpersOfOneInstallationAreOurs()
    {
        using var fixture = new TempInbox();
        var stick = PortableStick(fixture, "stick-a");
        var installed = InstalledCopy(fixture, "installed");

        // The same installation, whichever of its executables is asked about.
        Assert.True(AppLayout.IsSameInstallation(HelperOf(stick), HelperOf(stick)));
        Assert.True(AppLayout.IsSameInstallation(HelperOf(installed), HelperOf(installed)));
    }

    [Fact]
    public void AnUnreadablePathProvesNothingAndIsNeverAMatch()
    {
        using var fixture = new TempInbox();
        var stick = PortableStick(fixture, "stick-a");

        Assert.False(AppLayout.IsSameInstallation(null, HelperOf(stick)));
        Assert.False(AppLayout.IsSameInstallation(HelperOf(stick), null));
        Assert.False(AppLayout.IsSameInstallation("", HelperOf(stick)));
        Assert.Null(AppLayout.SuffixForExecutable(""));
    }

    /// <summary>
    /// The sweep itself is a few lines of process bookkeeping; the decision it
    /// makes is not. This keeps the decision wired to the sweep, so a later
    /// "simplification" cannot quietly go back to killing by process name.
    /// </summary>
    [Fact]
    public void TheKeeperSweepGoesThroughTheInstallationCheck()
    {
        var source = Path.Combine(RepoRoot(), "windows", "src", "WeChatBridge.ShareTarget", "SharePipe.cs");
        Assert.True(File.Exists(source), $"分享入口源码缺失：{source}");
        var text = File.ReadAllText(source);

        Assert.Contains("AppLayout.IsSameInstallation", text, StringComparison.Ordinal);
        Assert.Contains("other.MainModule?.FileName", text, StringComparison.Ordinal);
        // The check has to come before the kill: the old sweep killed every
        // process with the same executable name, whatever it was.
        var check = text.IndexOf("AppLayout.IsSameInstallation", StringComparison.Ordinal);
        var kill = text.IndexOf("other.Kill()", StringComparison.Ordinal);
        Assert.True(check >= 0 && kill > check,
            "分享入口的 keeper 清理必须在杀进程之前先判断安装归属。");
    }

    /// <summary>A portable installation: a marker file beside the helper directory.</summary>
    private static string PortableStick(TempInbox fixture, string name)
    {
        var root = Path.Combine(fixture.Root, name);
        Directory.CreateDirectory(Path.Combine(root, AppLayout.ShareTargetDirectoryName));
        File.WriteAllText(Path.Combine(root, AppLayout.PortableMarkerFileName), string.Empty);
        return root;
    }

    /// <summary>An installed copy: no marker file anywhere above the helper.</summary>
    private static string InstalledCopy(TempInbox fixture, string name)
    {
        var root = Path.Combine(fixture.Root, name);
        Directory.CreateDirectory(Path.Combine(root, AppLayout.ShareTargetDirectoryName));
        return root;
    }

    /// <summary>The helper executable inside an installation.</summary>
    private static string HelperOf(string installationRoot) =>
        Path.Combine(installationRoot, AppLayout.ShareTargetDirectoryName, "WeChatBridge.ShareTarget.exe");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows");
            if (Directory.Exists(candidate)) return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            $"windows was not found above the test output directory {AppContext.BaseDirectory}.");
    }
}
