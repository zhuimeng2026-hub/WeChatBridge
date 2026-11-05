using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Source-level contracts around the two names a release carries.
/// <para>
/// WPF resolves the <c>/AssemblyName;component/</c> segment of a pack URI by
/// calling <c>Assembly.Load</c> on that name. Renaming <c>&lt;AssemblyName&gt;</c>
/// without renaming every literal compiles clean, keeps every behavioural test
/// green, and produces a binary whose main window throws during
/// <c>InitializeComponent</c>: the process stays alive, no window appears, and
/// the single-instance mutex swallows every later double-click. That is exactly
/// what the 1.0.10 rename regressed, and nothing in the build caught it.
/// </para>
/// </summary>
public sealed class PackUriContractTests
{
    private static readonly Regex ComponentReference = new(
        @"/([A-Za-z0-9_.]+);component/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryComponentUriNamesTheAssemblyThatActuallyShips()
    {
        var expected = AssemblyName();
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            foreach (Match match in ComponentReference.Matches(File.ReadAllText(file)))
            {
                if (!string.Equals(match.Groups[1].Value, expected, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(file)}: {match.Value} -> expected /{expected};component/");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A pack URI names an assembly that does not ship. WPF loads that name while "
            + "parsing BAML, so the window never appears:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// pack-msix.ps1 overwrites the staged copy's Identity/Version, but the
    /// source manifest is also what a developer registers directly. Leaving it
    /// on the previous version makes a locally registered sparse package look
    /// like the old product even though the payload is the new one.
    /// </summary>
    [Fact]
    public void TheSparseManifestVersionTracksThePerpetualProductVersion()
    {
        var productVersion = PerpetualVersion();
        var manifestPath = Path.Combine(
            RepoRoot(), "windows", "packaging", "SparsePackage", "AppxManifest.xml");
        Assert.True(File.Exists(manifestPath), $"{manifestPath} is missing.");

        var ns = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        var identity = XDocument.Load(manifestPath).Root!.Element(ns + "Identity");
        Assert.NotNull(identity);

        var version = identity!.Attribute("Version");
        Assert.NotNull(version);
        Assert.Equal(productVersion + ".0", version!.Value);
    }

    private static string AssemblyName()
    {
        var project = Path.Combine(
            SrcRoot(), "WeChatBridge.Windows", "WeChatBridge.Windows.csproj");
        Assert.True(File.Exists(project), $"{project} is missing.");

        var name = XDocument.Load(project).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(name), $"{project} defines no <AssemblyName>.");
        return name!;
    }

    private static string PerpetualVersion()
    {
        var props = Path.Combine(RepoRoot(), "windows", "Directory.Build.props");
        Assert.True(File.Exists(props), $"{props} is missing.");

        var group = XDocument.Load(props).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PropertyGroup"
                && (string?)e.Attribute("Condition") is string condition
                && condition.Contains("'Perpetual'", StringComparison.Ordinal));
        Assert.NotNull(group);

        var version = group!.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(version),
            $"{props} defines no <Version> for the Perpetual edition.");
        return version!;
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            // obj\ holds generated markup that legitimately references the source.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Relative(string path) =>
        Path.GetRelativePath(SrcRoot(), path).Replace('\\', '/');

    private static string SrcRoot() => Path.Combine(RepoRoot(), "windows", "src");

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "windows", "src")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            $"windows/src was not found above {AppContext.BaseDirectory}.");
    }
}
