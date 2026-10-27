using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// WeChat forwards at most 100 messages per selection. A batch that lands
/// exactly on that number probably goes on, and the client says so instead of
/// letting the chat end at the platform's edge unnoticed.
/// </summary>
public sealed class WeChatForwardLimitTests
{
    [Fact]
    public void TheLimitIsTheNumberWeChatEnforces() => Assert.Equal(100, WeChatForwardLimit.Maximum);

    [Fact]
    public void ABatchOnTheCapIsFlagged() => Assert.True(WeChatForwardLimit.HitsLimit(WeChatForwardLimit.Maximum));

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(250)]
    public void ABatchOffTheCapIsNotFlagged(int count) => Assert.False(WeChatForwardLimit.HitsLimit(count));

    /// <summary>
    /// An archive whose transcript could not be read is not evidence of a full
    /// batch: the count is null then, and the client does not guess.
    /// </summary>
    [Fact]
    public void AnUnreadableTranscriptIsNeverFlagged() => Assert.False(WeChatForwardLimit.HitsLimit(null));

    [Fact]
    public void ABatchOfExactlyOneHundredMessagesReadsAsOnTheCap()
    {
        using var fixture = new TempInbox();
        var path = CollectionTests.CreateZip(fixture, "hundred.zip", Transcript(100));
        var metadata = CollectionBatchMetadata.Read([path]);
        Assert.Equal(100, metadata.Count);
        Assert.True(WeChatForwardLimit.HitsLimit(metadata.Count));
    }

    [Fact]
    public void ABatchBelowTheCapIsNotOnIt()
    {
        using var fixture = new TempInbox();
        var path = CollectionTests.CreateZip(fixture, "ninety-nine.zip", Transcript(99));
        var metadata = CollectionBatchMetadata.Read([path]);
        Assert.Equal(99, metadata.Count);
        Assert.False(WeChatForwardLimit.HitsLimit(metadata.Count));
    }

    /// <summary>
    /// A chat that really ends on its hundredth message gets the note once,
    /// wrongly. That is the deliberate trade: the app cannot tell "WeChat cut
    /// it here" from "the chat is exactly this long", so it says the batch
    /// sits on the cap rather than staying silent when the chat goes on.
    /// </summary>
    [Fact]
    public void AChatThatTrulyEndsOnTheCapIsStillFlagged()
    {
        Assert.True(WeChatForwardLimit.HitsLimit(WeChatForwardLimit.Maximum));
    }

    /// <summary>
    /// WeChat's cap is a hard ceiling, so a count past it cannot come from a
    /// single selection — and must not be read as "even more on the cap".
    /// </summary>
    [Fact]
    public void CountsAboveTheCapAreNotTreatedAsTheCap()
    {
        Assert.False(WeChatForwardLimit.HitsLimit(WeChatForwardLimit.Maximum + 1));
        Assert.False(WeChatForwardLimit.HitsLimit(int.MaxValue));
    }

    /// <summary>An empty selection is not a capped one.</summary>
    [Fact]
    public void AnEmptyBatchIsNotFlagged() => Assert.False(WeChatForwardLimit.HitsLimit(0));

    /// <summary>The two sentences the user reads name the number, in either language.</summary>
    [Fact]
    public void TheNoticeAndAdviceBothNameTheLimit()
    {
        Assert.Contains("100", WeChatForwardLimit.Notice);
        Assert.Contains("100", WeChatForwardLimit.Advice);
    }

    /// <summary>
    /// The English table is edited by hand and a missing entry silently renders
    /// Chinese in the English UI, so the two keys are asserted here rather
    /// than trusted.
    /// </summary>
    [Fact]
    public void BothSentencesAreTranslated()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("WeChatBridge.Windows.Core.Localization.en.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        Assert.True(translations.ContainsKey("正好 {0} 条，微信一次最多转 {1} 条，后面可能还有。"));
        Assert.True(translations.ContainsKey("复制这一批最后一句的文字，回微信搜索定位，从下一条接着多选（一次最多 {0} 条，重叠几条也没关系），再分享到「分批收集到律档通」。"));
    }

    private static string Transcript(int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
            text.Append($"·发言人\n2026年9月20日 09:{i % 60:00}\n第 {i} 条\n\n");
        return text.ToString();
    }
}
