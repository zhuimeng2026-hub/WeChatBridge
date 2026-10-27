namespace WeChatBridge.Windows.Core;

/// <summary>
/// WeChat's own multi-select forward stops at 100 messages — the 「最多选择100条」
/// button is the platform's, not ours. A batch that lands exactly on that number
/// therefore sits on the edge, and the chat most likely continues past it.
///
/// This is the same rule the iOS client shows (<c>SearchAnchor.forwardLimit</c>),
/// kept here so the Windows client reads one number rather than a second one.
/// </summary>
public static class WeChatForwardLimit
{
    /// <summary>Messages one WeChat selection can carry.</summary>
    public const int Maximum = 100;

    /// <summary>
    /// True when a batch sits exactly on the platform's cap. A transcript that
    /// could not be read (a null count) never claims it does: the app does not
    /// guess, and an unreadable archive is not evidence of a full batch.
    /// </summary>
    public static bool HitsLimit(int? count) => count == Maximum;

    /// <summary>The line that says the batch stopped where WeChat stops.</summary>
    // The interpolated string stays on the same line as the call: the
    // localization coverage scan reconstructs the lookup key from the
    // template that follows the call, so a line break in between would
    // hide these two keys from it.
    public static string Notice => L10n.Format($"正好 {Maximum} 条，微信一次最多转 {Maximum} 条，后面可能还有。");

    /// <summary>What to do about it: find the last line and carry on.</summary>
    public static string Advice => L10n.Format($"复制这一批最后一句的文字，回微信搜索定位，从下一条接着多选（一次最多 {Maximum} 条，重叠几条也没关系），再分享到「分批收集到律档通」。");
}
