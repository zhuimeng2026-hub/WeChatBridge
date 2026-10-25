using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Every user-visible string is its own translation key: the Chinese text is
/// the key, and <c>en.json</c> holds the English.
/// <para>
/// Two ways to write a string break that contract, and both fail silently —
/// the code compiles, the tests stay green, and the English UI shows
/// Chinese:
/// </para>
/// <list type="number">
/// <item>A key that is never in the table. The lookup misses, and
/// <c>L10n.Text</c> returns the key itself — the Chinese text.</item>
/// <item>A key that is built by interpolation. <c>L10n.Format</c> looks the
/// translation up by the *format string* of the interpolated expression, so
/// the hole has to stay a hole: <c>L10n.Format($"搜索失败：{error.Message}")</c>
/// looks up 「搜索失败：{0}」 and works, while
/// <c>L10n.Format($"复制到剪贴板时间：{timestamp}")</c> looks up
/// 「复制到剪贴板时间：2026-10-24 12:00:00 +08:00」, which no table can
/// contain.</item>
/// </list>
/// <para>
/// This scan reconstructs the key of every call site and asserts the table
/// really has it. It is a source scan rather than a runtime check because the
/// broken form is invisible from the call site: the value that would have
/// been the key is only known at runtime.
/// </para>
/// </summary>
public sealed class LocalizationCoverageTests
{
    [Fact]
    public void EveryLiteralLocalizationKeyExistsInTheTranslationTable()
    {
        var translations = Translations();
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var relative = Relative(file);

            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                // Both quoting styles are in use: `Loc Key='…'` inside markup
                // extension text content, `Loc Key="…"` as an XML attribute.
                foreach (Match match in Regex.Matches(text, @"Loc\s+Key=(?:'([^']*)'|""([^""]*)"")"))
                {
                    var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    if (!translations.ContainsKey(key))
                        offenders.Add($"{relative}: 「{key}」");
                }
                continue;
            }

            // Every string literal inside an L10n.Text(...) argument is a
            // lookup key. Reading the whole argument (not just a literal
            // right after the parenthesis) is what catches the switch arms
            // and ternary arms, where the key sits several tokens deep.
            foreach (var key in TextArguments(text))
            {
                if (!IsChineseKey(key)) continue;
                if (!translations.ContainsKey(key))
                    offenders.Add($"{relative}: 「{key}」");
            }

            foreach (var template in FormatTemplates(text))
            {
                if (!IsChineseKey(template)) continue;
                if (!translations.ContainsKey(template))
                    offenders.Add($"{relative}: format template 「{template}」");
            }
        }

        Assert.True(offenders.Count == 0,
            "These localization keys are not in en.json, so the English UI shows the Chinese text:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A translation that still contains Chinese, or that is empty, is just as
    /// broken as a missing one: the lookup succeeds and the Chinese comes out
    /// anyway. The product name is the one Chinese word the English UI keeps
    /// on purpose — but it is written "WeChatBridge", never 「律档通」.
    /// </summary>
    [Fact]
    public void EveryTranslationIsEnglishAndNotEmpty()
    {
        var offenders = new List<string>();
        foreach (var (key, translation) in Translations())
        {
            if (string.IsNullOrWhiteSpace(translation))
                offenders.Add($"{key}: empty");
            else if (translation.Any(character => character >= '一' && character <= '鿿'))
                offenders.Add($"{key}: {translation}");
        }

        Assert.True(offenders.Count == 0,
            "These English translations still contain Chinese:\n  " + string.Join("\n  ", offenders));
    }

    private static Dictionary<string, string> Translations()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream(
            "WeChatBridge.Windows.Core.Localization.en.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    /// <summary>
    /// A localization key is the Chinese text itself, so only a literal that
    /// contains Chinese can be one. The other literals inside an
    /// <c>L10n.Text(...)</c> argument are values being compared (a phase
    /// name, an id), not keys.
    /// </summary>
    private static bool IsChineseKey(string value) =>
        value.Any(character => character >= '一' && character <= '鿿');

    /// <summary>
    /// Every string literal that appears inside an <c>L10n.Text(...)</c>
    /// argument. The argument may be a plain literal, a ternary
    /// (<c>L10n.Text(flag ? "A" : "B")</c>) or a switch arm
    /// (<c>L10n.Text(state switch { … => "A" })</c>); in all three the
    /// literal is a lookup key, and all three fail the same way when the
    /// table does not have it.
    /// </summary>
    private static IEnumerable<string> TextArguments(string text)
    {
        const string Marker = "L10n.Text(";
        var index = 0;
        while (true)
        {
            var start = text.IndexOf(Marker, index, StringComparison.Ordinal);
            if (start < 0) yield break;

            var cursor = start + Marker.Length;
            var depth = 1;
            var argument = new StringBuilder();
            while (cursor < text.Length && depth > 0)
            {
                var character = text[cursor];
                if (character == '"')
                {
                    var literal = new StringBuilder();
                    literal.Append(character);
                    cursor++;
                    while (cursor < text.Length && text[cursor] != '"')
                    {
                        if (text[cursor] == '\\' && cursor + 1 < text.Length)
                        {
                            literal.Append(text[cursor]);
                            literal.Append(text[cursor + 1]);
                            cursor += 2;
                            continue;
                        }
                        literal.Append(text[cursor]);
                        cursor++;
                    }
                    literal.Append('"');
                    argument.Append(literal);
                    cursor++;
                    continue;
                }
                if (character == '(') depth++;
                else if (character == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
                argument.Append(character);
                cursor++;
            }

            foreach (Match match in Regex.Matches(argument.ToString(), @"""((?:[^""\\]|\\.)*)"""))
                yield return Unescape(match.Groups[1].Value);

            index = cursor + 1;
        }
    }

    /// <summary>
    /// Reconstruct the lookup key of every <c>L10n.Format($"…")</c> call: the
    /// literal text with each hole replaced by <c>{0}</c>, <c>{1}</c>, … in
    /// order — which is exactly what <c>FormattableString.Format</c> hands to
    /// the translation lookup.
    /// </summary>
    private static IEnumerable<string> FormatTemplates(string text)
    {
        const string Marker = "L10n.Format($\"";
        var index = 0;
        while (true)
        {
            var start = text.IndexOf(Marker, index, StringComparison.Ordinal);
            if (start < 0) yield break;

            var cursor = start + Marker.Length;
            var template = new StringBuilder();
            var argument = 0;
            var closed = false;
            while (cursor < text.Length)
            {
                var character = text[cursor];
                if (character == '"') { closed = true; break; }
                if (character == '\\')
                {
                    template.Append(character);
                    if (cursor + 1 < text.Length) template.Append(text[cursor + 1]);
                    cursor += 2;
                    continue;
                }
                if (character == '{')
                {
                    if (cursor + 1 < text.Length && text[cursor + 1] == '{')
                    {
                        template.Append('{');
                        cursor += 2;
                        continue;
                    }
                    var hole = SkipHole(text, cursor);
                    template.Append('{').Append(argument++);
                    // The format specifier is part of the lookup key: the
                    // compiler keeps it in FormattableString.Format
                    // ("收集 · {0:MM-dd HH:mm}"), so the translation has to
                    // carry it too or the date renders in full.
                    if (hole.Specifier.Length > 0) template.Append(':').Append(hole.Specifier);
                    template.Append('}');
                    cursor = hole.End;
                    continue;
                }
                if (character == '}' && cursor + 1 < text.Length && text[cursor + 1] == '}')
                {
                    template.Append('}');
                    cursor += 2;
                    continue;
                }
                template.Append(character);
                cursor++;
            }

            if (closed) yield return Unescape(template.ToString());
            index = cursor + 1;
        }
    }

    /// <summary>
    /// Skip an interpolation hole, which may itself contain string literals
    /// (<c>{x.ToString("a,b")}</c>), nested braces and a ternary. The format
    /// specifier that follows the expression is returned separately: it is
    /// part of the lookup key.
    /// </summary>
    private static (int End, string Specifier) SkipHole(string text, int open)
    {
        var depth = 0;
        var cursor = open;
        var specifierStart = -1;
        var ternary = false;
        while (cursor < text.Length)
        {
            var character = text[cursor];
            if (character == '"')
            {
                cursor++;
                while (cursor < text.Length && text[cursor] != '"')
                {
                    if (text[cursor] == '\\') cursor++;
                    cursor++;
                }
                cursor++;
                continue;
            }
            if (character == '{') depth++;
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                    return (cursor + 1, specifierStart < 0 ? "" : text[specifierStart..cursor]);
            }
            else if (character == '?' && depth == 1)
            {
                ternary = true;
            }
            else if (character == ':' && depth == 1)
            {
                // A ternary's colon is not a format specifier.
                if (ternary) ternary = false;
                else if (specifierStart < 0) specifierStart = cursor + 1;
            }
            cursor++;
        }
        return (cursor, "");
    }

    private static string Unescape(string literal)
    {
        if (!literal.Contains('\\')) return literal;
        var builder = new StringBuilder(literal.Length);
        for (var index = 0; index < literal.Length; index++)
        {
            if (literal[index] != '\\' || index + 1 >= literal.Length)
            {
                builder.Append(literal[index]);
                continue;
            }
            var next = literal[++index];
            builder.Append(next switch
            {
                'n' => "\n",
                'r' => "\r",
                't' => "\t",
                '"' => "\"",
                '\\' => "\\",
                '0' => "\0",
                _ => "\\" + next,
            });
        }
        return builder.ToString();
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(RepoRoot(), "windows", "src");
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

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
