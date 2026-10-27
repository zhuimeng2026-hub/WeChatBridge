using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>Ported from ObsidianNoteTests.swift.</summary>
public sealed class ObsidianNoteTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void RendersFrontmatterAndTranscriptLinks()
    {
        var body = "·甲\n2026年9月20日 09:10\n第一条\n\n·乙\n2026年9月20日 09:11\n第二条\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        Assert.Equal(
            "项目群 (8)的聊天",
            ObsidianNote.Title("项目群 (8)", transcript, "Zip归档.zip"));

        var note = ObsidianNote.Render(
            "项目群 (8)的聊天",
            "项目群 (8)",
            "项目周会",
            DateTimeOffset.UnixEpoch,
            transcript,
            "项目群.zip",
            timeZone: Utc);

        Assert.Contains("title: \"项目群 (8)的聊天\"", note);
        Assert.Contains("chat: \"项目群 (8)\"", note);
        Assert.Contains("scene: \"项目周会\"", note);
        Assert.Contains("archive: \"附件/项目群.zip\"", note);
        Assert.Contains("[[附件/项目群.zip]]", note);
        Assert.Contains("**甲**", note);
        Assert.Contains("第一条", note);
    }

    [Fact]
    public void EmbedsAttachmentAtItsMessage()
    {
        var body = "·甲\n2026年9月20日 09:10\n[图片] 微信图片_202609202355_1.jpg\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        var note = ObsidianNote.Render(
            "项目群的聊天",
            "项目群",
            null,
            DateTimeOffset.UnixEpoch,
            transcript,
            "项目群.zip",
            attachments: new Dictionary<string, string>
            {
                ["微信图片_202609202355_1.jpg"] = "微信图片_202609202355_1.jpg",
            },
            timeZone: Utc);

        Assert.Contains("[图片] 微信图片_202609202355_1.jpg\n\n![[附件/微信图片_202609202355_1.jpg]]", note);
    }

    [Fact]
    public void LongerAttachmentNameWinsWithoutAlsoMatchingASubstring()
    {
        var body = "·甲\n2026年9月20日 09:10\n[图片] 微信图片_202609202355_11.jpg\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        var note = ObsidianNote.Render(
            "项目群的聊天",
            "项目群",
            null,
            DateTimeOffset.UnixEpoch,
            transcript,
            "项目群.zip",
            attachments: new Dictionary<string, string>
            {
                ["微信图片_202609202355_1.jpg"] = "微信图片_202609202355_1.jpg",
                ["微信图片_202609202355_11.jpg"] = "微信图片_202609202355_11.jpg",
            },
            timeZone: Utc);

        Assert.Contains("![[附件/微信图片_202609202355_11.jpg]]", note);
        Assert.DoesNotContain("![[附件/微信图片_202609202355_1.jpg]]", note);
    }

    [Fact]
    public void OcrTitleWinsOverTranscriptParticipants()
    {
        var body = "·甲\n2026年9月20日 09:10\n第一条\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        Assert.Equal(
            "AI先行者联盟的聊天",
            ObsidianNote.Title("AI先行者联盟", transcript, "微信聊天记录.zip"));
    }

    [Fact]
    public void QuotesYamlSpecialCharacters()
    {
        Assert.Equal("Zip归档", ObsidianNote.Title(null, null, "Zip归档.zip"));

        var note = ObsidianNote.Render(
            "a \"quoted\" title",
            null,
            null,
            DateTimeOffset.UnixEpoch,
            null,
            "chat.zip",
            timeZone: Utc);

        Assert.Contains("title: \"a \\\"quoted\\\" title\"", note);
        Assert.Contains("未包含可识别的聊天正文", note);
    }

    private static WeChatNativeArchive.Transcript Txt(string body) =>
        new("聊天记录.txt", body, WeChatTranscriptRecord.Parse(body, Utc));

    private static string Rendered(WeChatNativeArchive.Transcript transcript) =>
        ObsidianNote.Render("群的聊天", "群", null,
            DateTimeOffset.UnixEpoch, transcript, "a.zip", timeZone: Utc);

    [Fact]
    public void MergeAppendsOnlyTheNewRecords()
    {
        var first = Txt("·甲\n2026年9月20日 09:10\n第一条\n\n·乙\n2026年9月20日 09:11\n第二条\n");
        var note = Rendered(first);
        var second = Txt("·乙\n2026年9月20日 09:11\n第二条\n\n·丙\n2026年9月20日 09:12\n第三条\n");

        var outcome = ObsidianNote.TryMerge(
            note, second, null, "b.zip", "群", null,
            new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
            out var merged, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);
        Assert.NotNull(merged);
        Assert.Contains("messages: 3", merged);
        Assert.Contains("exported: 2026-09-21 00:00", merged);
        Assert.Contains("> 追加归档：[[附件/b.zip]]", merged);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(merged!, "第二条"));
        Assert.Contains("第三条", merged);
    }

    /// <summary>
    /// The same chat shared again, from the same archive: the messages are
    /// known and the archive is already recorded, so there is nothing new.
    /// A <em>different</em> archive is new content — the note records where
    /// each copy came from (see the archive-provenance tests in
    /// KnowledgeDeliveryTests).
    /// </summary>
    [Fact]
    public void MergeReportsNothingNewWhenEverythingIsAlreadyThere()
    {
        var transcript = Txt("·甲\n2026年9月20日 09:10\n第一条\n");
        var outcome = ObsidianNote.TryMerge(
            Rendered(transcript), transcript, null, "a.zip", "群", null,
            DateTimeOffset.UnixEpoch, out var merged, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.NothingNew, outcome);
        Assert.Null(merged);
    }

    [Fact]
    public void MergeRejectsANoteItDidNotWrite()
    {
        var transcript = Txt("·甲\n2026年9月20日 09:10\n第一条\n");
        var outcome = ObsidianNote.TryMerge(
            "# 我自己的笔记\n\n没有 front matter。\n", transcript, null, "b.zip",
            "群", null, DateTimeOffset.UnixEpoch, out var merged, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.NotApplicable, outcome);
        Assert.Null(merged);
    }

    [Fact]
    public void MergeKeepsASecondMessageFromTheSameMinute()
    {
        var first = Txt("·甲\n2026年9月20日 09:10\n在吗\n");
        var second = Txt("·甲\n2026年9月20日 09:10\n在吗\n\n·甲\n2026年9月20日 09:10\n看到了吗\n");

        var outcome = ObsidianNote.TryMerge(
            Rendered(first), second, null, "b.zip", "群", null,
            DateTimeOffset.UnixEpoch, out var merged, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);
        Assert.Contains("看到了吗", merged);
        Assert.Contains("messages: 2", merged);
    }

    [Fact]
    public void MergeFillsMissingFrontMatter()
    {
        var transcript = Txt("·甲\n2026年9月20日 09:10\n第一条\n");
        // A note written before the title read worked: no chat/scene lines.
        var note = ObsidianNote.Render("甲的聊天", null, null,
            DateTimeOffset.UnixEpoch, transcript, "a.zip", timeZone: Utc);
        var second = Txt("·甲\n2026年9月20日 09:10\n第一条\n\n·甲\n2026年9月20日 09:11\n新消息\n");

        var outcome = ObsidianNote.TryMerge(
            note, second, null, "b.zip", "甲", "项目周会",
            DateTimeOffset.UnixEpoch, out var merged, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);
        Assert.Contains("chat: \"甲\"", merged);
        Assert.Contains("scene: \"项目周会\"", merged);
    }

    /// <summary>
    /// Re-forwarding the same conversation must not duplicate messages.
    /// <para>
    /// The merge marker (「追加归档：[[附件/x.zip]]」) is written between two
    /// messages, so it used to become part of the message above it. That
    /// message then never matched the incoming record again, and every further
    /// share of the same chat appended one more copy of it.
    /// </para>
    /// </summary>
    [Fact]
    public void ReSharingTheSameChatDoesNotDuplicateMessages()
    {
        var first = Txt("·甲\n2026年9月20日 09:10\n第一条\n");
        var second = Txt("·甲\n2026年9月20日 09:10\n第一条\n\n·乙\n2026年9月20日 09:11\n第二条\n");
        var note = Rendered(first);

        // The second share adds 乙 and writes the marker after 甲.
        var outcome = ObsidianNote.TryMerge(
            note, second, null, "b.zip", "群", null,
            new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero), out var merged, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);
        Assert.NotNull(merged);
        // The marker is part of the note the user reads.
        Assert.Contains("> 追加归档：[[附件/b.zip]]", merged!);

        // The third share of the same chat adds no message: 甲's body carries
        // the marker, and the marker is not part of the message. The new
        // archive is still provenance, so the merge happens and the note
        // records where this copy came from.
        outcome = ObsidianNote.TryMerge(
            merged!, second, null, "c.zip", "群", null,
            new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), out var again, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);
        Assert.NotNull(again);
        Assert.Contains("> 追加归档：[[附件/c.zip]]", again!);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again!, "第一条"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again!, "第二条"));
    }

    /// <summary>
    /// A share that overlaps an earlier one still adds only what is missing,
    /// even after the note already carries a merge marker.
    /// </summary>
    [Fact]
    public void AnOverlappingShareAddsOnlyTheMissingMessage()
    {
        var first = Txt("·甲\n2026年9月20日 09:10\n第一条\n");
        var second = Txt("·甲\n2026年9月20日 09:10\n第一条\n\n·乙\n2026年9月20日 09:11\n第二条\n");
        var third = Txt("·甲\n2026年9月20日 09:10\n第一条\n\n·乙\n2026年9月20日 09:11\n第二条\n\n·丙\n2026年9月20日 09:12\n第三条\n");

        var outcome = ObsidianNote.TryMerge(
            Rendered(first), second, null, "b.zip", "群", null,
            DateTimeOffset.UnixEpoch, out var merged, Utc);
        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);

        outcome = ObsidianNote.TryMerge(
            merged!, third, null, "c.zip", "群", null,
            DateTimeOffset.UnixEpoch, out var again, Utc);

        Assert.Equal(ObsidianNote.MergeOutcome.Merged, outcome);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again!, "第一条"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again!, "第二条"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again!, "第三条"));
    }
}
