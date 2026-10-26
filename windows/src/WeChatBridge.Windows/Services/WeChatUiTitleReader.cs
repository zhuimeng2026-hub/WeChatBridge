using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Reads the current chat name out of the WeChat main window through UI
/// Automation — the Windows answer to macOS's Accessibility read.
///
/// Window titles do not work here: on Windows, WeChat 4.x (Weixin) titles its
/// main window 「微信」 regardless of which conversation is open — that is how
/// groups ended up bound to the literal name "微信". The mmui::MainWindow
/// class does expose a full UIA tree, and the header row's Text element carries
/// the live chat name, including the member count 「群名 (260)」 for group
/// chats. We read element names only — the same privacy boundary as the macOS
/// path: no capture, no injection, and only the window already on screen.
/// </summary>
public static class WeChatUiTitleReader
{
    /// <summary>
    /// Records why a title could not be read.
    /// <para>
    /// A missing group name is a support question ("the chat is called 微信
    /// instead of its name"), and the answer lives in here: which reader was
    /// tried, whether the WeChat window was found, and what the automation
    /// tree said when it was not.
    /// </para>
    /// </summary>
    private static void Log(LogLevel level, string message, Exception? error = null) =>
        DiagnosticSink.Write(level, message, error);

    public static async Task<GroupTitleParser.Title?> ReadWithFallbackAsync()
    {
        var title = await Task.Run(() => TryRead() ?? new WeChatWindowTitleReader().TryRead());
        if (title is not null) return title;
        try
        {
            return await WeChatOcrTitleReader.TryReadAsync();
        }
        catch (Exception error)
        {
            // The OCR fallback is the last reader. When it fails too, the chat
            // name stays empty and the archive is filed under a generic name.
            Log(LogLevel.Warn, "所有群名读取方式均失败，聊天记录将使用默认名称。", error);
            return null;
        }
    }
    /// <summary>Weixin.exe is WeChat 4.x; WeChat.exe is the 3.x line.</summary>
    private static readonly string[] WeChatProcessNames = ["weixin", "wechat"];

    private static readonly Regex MemberCount =
        new(@"^(.+?)[（(]\s*(\d{1,5})\s*[)）]\s*$", RegexOptions.Compiled);

    /// <summary>Best-effort read; every failure mode means "no group name".</summary>
    public static GroupTitleParser.Title? TryRead()
    {
        try
        {
            return Read();
        }
        catch (Exception error)
        {
            Log(LogLevel.Warn, "通过 UI Automation 读取微信群名失败。", error);
            return null;
        }
    }

    /// <summary>
    /// Reads the main window's Text elements in tree order. A 「name (count)」
    /// line is preferred — it is unambiguously a group header; a plain first
    /// Text element covers direct chats, which carry no member count.
    /// </summary>
    public static GroupTitleParser.Title? Read()
    {
        var candidates = ChatTitleCandidates();
        if (candidates.Count == 0)
            return null;
        foreach (var candidate in candidates)
        {
            var match = MemberCount.Match(candidate);
            if (match.Success && int.TryParse(match.Groups[2].Value, out var count))
                return new GroupTitleParser.Title(match.Groups[1].Value.Trim(), count);
        }
        return new GroupTitleParser.Title(candidates[0], null);
    }

    /// <summary>
    /// Text-element names of the WeChat main window, in tree order. The first
    /// Text descendant is the conversation header (verified against
    /// mmui::MainWindow on Weixin 4.x); rows below it repeat the name split
    /// into 「name」 and 「(count)」 parts.
    /// </summary>
    private static IReadOnlyList<string> ChatTitleCandidates()
    {
        var window = FindMainWindow();
        if (window is null)
        {
            // The most common reason this happens is a WeChat version whose
            // main window no longer reports the class name we look for, or a
            // process we cannot inspect. Recording what we did see turns "the
            // group name is wrong" into a solvable question.
            Log(LogLevel.Warn,
                "未找到微信主窗口（mmui::MainWindow）。"
                + $"已见进程: {SeenWeChatProcesses()}");
            return [];
        }
        try
        {
            var texts = window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var names = new List<string>(texts.Count);
            foreach (AutomationElement element in texts)
            {
                try
                {
                    var name = element.Current.Name?.Trim();
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
                catch (Exception error)
                {
                    // An element vanishing mid-read loses its slot, not the scan.
                    Log(LogLevel.Info, "读取 UI Automation 文本元素时该元素已失效。", error);
                }
            }
            return names;
        }
        catch (Exception error)
        {
            Log(LogLevel.Warn, "枚举微信窗口的文本元素失败。", error);
            return [];
        }
    }

    /// <summary>
    /// The WeChat processes that were running, with their window class names.
    /// When the class check fails, the class name that was actually found is
    /// the clue: a WeChat build that renamed its main window shows up here.
    /// </summary>
    private static string SeenWeChatProcesses()
    {
        var seen = new List<string>();
        foreach (var name in WeChatProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception error)
            {
                seen.Add($"{name}: 无法枚举 ({error.GetType().Name})");
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == 0)
                    {
                        seen.Add($"{name}: 无主窗口");
                        continue;
                    }
                    try
                    {
                        var element = AutomationElement.FromHandle(hwnd);
                        seen.Add($"{name}: class={element.Current.ClassName}");
                    }
                    catch (Exception error)
                    {
                        seen.Add($"{name}: 无法读取窗口类名 ({error.GetType().Name})");
                    }
                }
            }
        }
        return seen.Count == 0 ? "(无微信进程)" : string.Join("; ", seen);
    }

    /// <summary>
    /// The WeChat main window as a UIA element: any weixin/wechat process whose
    /// main window reports the mmui::MainWindow class. Side dialogs (the 「发送给」
    /// share sheet included) fail the class check, so a share in flight cannot
    /// mislabel the group.
    /// </summary>
    private static AutomationElement? FindMainWindow()
    {
        foreach (var name in WeChatProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception error)
            {
                Log(LogLevel.Info, $"枚举 {name} 进程失败。", error);
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == 0)
                        continue;
                    try
                    {
                        var element = AutomationElement.FromHandle(hwnd);
                        if (element.Current.ClassName is "mmui::MainWindow")
                            return element;
                    }
                    catch (Exception error)
                    {
                        // Not a UIA-backed window — try the next process.
                        Log(LogLevel.Info, "该微信进程的主窗口不支持 UI Automation。", error);
                    }
                }
            }
        }
        return null;
    }
}
