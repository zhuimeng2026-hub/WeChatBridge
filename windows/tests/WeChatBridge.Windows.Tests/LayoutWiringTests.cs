using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Contract tests over the repository's own source, in the spirit of
/// <c>PackagingAssetsTests</c>: they assert how things are wired rather than
/// exercising runtime behaviour, which is what stops the two failures that are
/// invisible until a user is holding a USB stick.
/// </summary>
public sealed class LayoutWiringTests
{
    /// <summary>
    /// Files allowed to name LocalApplicationData directly.
    /// <para>
    /// Everything else must go through <c>AppLayout</c>, or a portable build
    /// writes some of its state to the stick and some to the machine — and the
    /// split is invisible: the app looks fine until the drive is pulled.
    /// </para>
    /// </summary>
    private static readonly string[] AllowedLocalAppDataFiles =
    [
        "WeChatBridge.Windows.Core/AppLayout.cs",
        // A file-dialog start folder, not a place user data is written.
        "WeChatBridge.Windows/Components/CollectionDialogs.cs",
        // Reads the installed copy's presence so the UI can warn that taking over
        // the share entry disables it.
        "WeChatBridge.Windows/Services/ShareRegistrationService.cs",
    ];

    [Fact]
    public void NoSourceFileResolvesLocalAppDataOutsideTheAllowlist()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var relative = Relative(file);
            if (AllowedLocalAppDataFiles.Any(a => relative.EndsWith(a, StringComparison.Ordinal)))
                continue;
            if (File.ReadAllText(file).Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These files resolve %LOCALAPPDATA% directly instead of through AppLayout, "
            + "which sends that data to the stick in a portable build:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The portable sentinel is deliberately not a constant that could be
    /// duplicated. Two definitions would let the build script's file and the
    /// resolver's file drift, and the symptom is a build that silently behaves
    /// as installed.
    /// </summary>
    [Fact]
    public void ThePortableMarkerNameIsDefinedExactlyOnce()
    {
        var definitions = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains(AppLayout.PortableMarkerFileName, StringComparison.Ordinal))
            .Select(Relative)
            .Where(relative => relative.EndsWith("AppLayout.cs", StringComparison.Ordinal) == false)
            .ToList();

        Assert.True(definitions.Count == 0,
            "portable.flag is named outside AppLayout, where it can drift from the resolver:\n  "
            + string.Join("\n  ", definitions));
    }

    /// <summary>
    /// Every machine-wide coordination name carries the layout suffix. Without
    /// it a portable copy and an installed copy evict each other in silence: the
    /// second launch creates its Inbox, loses the single-instance mutex, and
    /// exits with no window.
    /// </summary>
    [Theory]
    [InlineData("WeChatBridge.Windows/App.xaml.cs")]
    [InlineData("WeChatBridge.ShareTarget/Program.cs")]
    [InlineData("WeChatBridge.Windows.Core/PrefetchHint.cs")]
    [InlineData("WeChatBridge.Windows.Core/Collections/CollectionImportProgress.cs")]
    [InlineData("WeChatBridge.Windows/LaunchAtLogin.cs")]
    public void CoordinationNamesAreSuffixedByLayout(string relativePath)
    {
        var text = File.ReadAllText(SourceFile(relativePath));

        Assert.Contains("CoordinationSuffix", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePortableRegistrationScriptShipsWithTheBuild()
    {
        var script = Path.Combine(PortableRoot(), "Register-ShareTarget.ps1");

        Assert.True(File.Exists(script), $"{script} is missing; the portable build cannot register a share entry.");
        // Registration is launched by the app with -SkipProcessKill, because the
        // script's Stop-Process would otherwise kill the app whose button ran it.
        Assert.Contains("SkipProcessKill", File.ReadAllText(script), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstRunScriptShipsWithAByteOrderMark()
    {
        var script = Path.Combine(PortableRoot(), "FirstRun.ps1");

        Assert.True(File.Exists(script), $"{script} is missing.");
        // Windows PowerShell 5.1 reads a BOM-less .ps1 as the system ANSI code
        // page — GBK on a Chinese Windows — which silently corrupts the Chinese
        // strings instead of failing.
        var bytes = File.ReadAllBytes(script);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "FirstRun.ps1 must be UTF-8 with a BOM.");
    }

    [Fact]
    public void EveryPowerShellScriptWithChineseStringsShipsWithAByteOrderMark()
    {
        // The rule this guards: a .ps1 that carries Chinese *in code* — not in a
        // comment — must be UTF-8 with a BOM, because Windows PowerShell 5.1
        // reads a BOM-less .ps1 as the system ANSI code page (GBK on a Chinese
        // Windows) and corrupts the string instead of failing.
        //
        // register-dev.ps1 was the one that got it wrong: it writes 失败 / 成功
        // into its registration log, and the log came out as mojibake. The
        // BOM-less test only guarded FirstRun.ps1, so nothing caught it.
        //
        // Comments are excluded on purpose. A Chinese comment in a BOM-less
        // file is only a cosmetic problem, and the three files that have one
        // (build-installer.ps1, msi/build-bundle.ps1, msi/Register-ShareTarget.ps1)
        // are left as they are rather than carrying a fix nobody asked for.
        var offenders = new List<string>();
        foreach (var script in PowerShellScripts())
        {
            var text = File.ReadAllText(script);
            if (!HasNonAsciiOutsideComments(text))
                continue;
            if (HasByteOrderMark(File.ReadAllBytes(script)))
                continue;
            offenders.Add(RelativeToRepo(script));
        }

        Assert.True(offenders.Count == 0,
            "These .ps1 files carry Chinese outside comments and have no BOM, so Windows " +
            "PowerShell 5.1 reads them as ANSI and corrupts the strings: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// Whether the file carries any character outside ASCII.
    /// </summary>
    private static bool HasNonAsciiOutsideComments(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            // A '#' line is a comment. A block comment (<# #>) is not handled
            // here: no script in the tree puts Chinese inside one, and a
            // half-parsing block comment would be a worse failure than
            // tolerating a false negative.
            if (trimmed.StartsWith('#'))
                continue;
            if (trimmed.Length > 0 && trimmed.Any(c => c > 127))
                return true;
        }
        return false;
    }

    private static bool HasByteOrderMark(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static IEnumerable<string> PowerShellScripts() =>
        Directory.EnumerateFiles(WindowsRoot(), "*.ps1", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string RelativeToRepo(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string WindowsRoot() =>
        Path.Combine(RepoRoot(), "windows");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows");
            if (Directory.Exists(candidate))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            $"windows was not found above the test output directory {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// The portable build is the free tier of the buy-out edition, so it is
    /// built from the buy-out source set only.
    /// <para>
    /// A comment in the script is not enough of a guard: adding an
    /// <c>-Edition</c> parameter to it is a one-line change that looks
    /// harmless, compiles clean, and lets a subscription build reach a USB
    /// stick — where the licence travels with the drive and the account does
    /// not. Nothing else in the build would catch it.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePortableBuildOnlyCompilesTheBuyoutEdition()
    {
        var script = Path.Combine(PortableRoot(), "build-portable.ps1");
        Assert.True(File.Exists(script), $"{script} is missing.");

        var text = File.ReadAllText(script);

        Assert.Contains("WeChatBridgeEdition", text, StringComparison.Ordinal);
        Assert.Contains("'Perpetual'", text, StringComparison.Ordinal);
        // ValidateSet is what an -Edition parameter needs, so its absence is
        // the thing being asserted: there is no parameter to pass.
        Assert.DoesNotContain("ValidateSet", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            // obj\ holds generated markup that legitimately references the source.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string SourceFile(string relativePath) =>
        Path.Combine(SrcRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Relative(string path) =>
        Path.GetRelativePath(SrcRoot(), path).Replace('\\', '/');

    private static string SrcRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows", "src");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"windows/src was not found above the test output directory {AppContext.BaseDirectory}.");
    }

    private static string PortableRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows", "portable");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"windows/portable was not found above the test output directory {AppContext.BaseDirectory}.");
    }
}
