using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void TranslationsPreserveEveryFormatArgumentAndUseValidCompositeFormats()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("WeChatBridge.Windows.Core.Localization.en.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        var placeholder = new Regex(@"(?<!\{)\{(\d+)(?:[^{}]*)\}");
        foreach (var (key, translation) in translations)
        {
            var arguments = placeholder.Matches(key).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
            Assert.Equal(arguments, placeholder.Matches(translation).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray());
            if (arguments.Length > 0)
            {
                var values = Enumerable.Range(0, arguments.Max() + 1).Select(_ => (object)DateTime.UnixEpoch).ToArray();
                var error = Record.Exception(() => string.Format(translation, values));
                Assert.True(error is null, $"Invalid translation format: {key} -> {translation}: {error}");
            }
        }
        Assert.Equal("Save to Folder", translations["沉淀到文件夹"]);
        Assert.Equal("Batch collection", translations["分批收集"]);
    }

    /// <summary>
    /// The three workbench strings that used to reach the English UI as Chinese.
    /// They are asserted by value rather than by presence: a key that is present
    /// but empty, or that translates to the Chinese text, is just as broken as a
    /// missing one.
    /// </summary>
    [Fact]
    public void WorkbenchStringsThatUsedToLeakChineseAreTranslated()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("WeChatBridge.Windows.Core.Localization.en.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;

        // L-1: the lookup key is the compiler-generated template of
        // `$"筛选失败：{error.Message}"`, so it carries the {0} placeholder.
        Assert.Equal("Filter failed: {0}", translations["筛选失败：{0}"]);
        // L-2: the save-folder default, which doubles as a directory name.
        Assert.Equal("Analysis", translations["分析"]);
        // L-3: the tool dropdown's "其他" entry.
        Assert.Equal("Other", translations["其他"]);
    }

    /// <summary>
    /// 「分析草稿」 is a suffix appended to the material's file name. The Chinese
    /// needs no separator, English needs a space so it does not run into the
    /// name — the same convention the repository already uses for
    /// 「（含当前选中段落）」.
    /// </summary>
    [Fact]
    public void TheDraftSuffixCarriesTheSeparatorEnglishNeeds()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("WeChatBridge.Windows.Core.Localization.en.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;

        Assert.Equal(" Analysis draft", translations["分析草稿"]);
    }

    /// <summary>
    /// The note the workbench writes is a document the user takes away, so it
    /// speaks the interface's language rather than always Chinese. These are the
    /// strings it is assembled from.
    /// </summary>
    [Fact]
    public void TheWorkbenchNoteIsWrittenInTheInterfaceLanguage()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("WeChatBridge.Windows.Core.Localization.en.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;

        Assert.Equal("Analysis result", translations["分析结果"]);
        Assert.Equal("Request log", translations["请求记录"]);
        Assert.Equal("Source material", translations["参考材料"]);
        Assert.Equal("(no material selected)", translations["（未选择材料）"]);
        Assert.Equal("Copied to the clipboard at: {0}", translations["复制到剪贴板时间：{0}"]);
        Assert.Equal("Selected tool: {0}", translations["选择的工具：{0}"]);
        Assert.Equal("Selected passage: {0}", translations["选中段落：{0}"]);
        Assert.Equal("Sample materials", translations["示例资料"]);
        Assert.Equal("Sample materials/Project background.md", translations["示例资料/项目背景.md"]);
    }
}
