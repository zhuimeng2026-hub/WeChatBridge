using Microsoft.Win32;
using System.Diagnostics;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// 开机自启 via HKCU\…\CurrentVersion\Run — the mechanism Windows provides for
/// exactly this. The registered command carries <c>--background</c> so sign-in
/// never raises a window: the process exists to consume share intents, not to
/// be looked at.
/// </summary>
internal static class LaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>
    /// Suffixed so a portable copy disabling autostart deletes its own value
    /// rather than the installed build's. Installed builds keep the plain name.
    /// The name is visible in Task Manager's Startup tab, so it carries the
    /// product's name rather than the project the code was written under.
    /// </summary>
    private static string ValueName => "Lvdangtong.Windows" + AppLayout.Current.CoordinationSuffix;

    /// <summary>
    /// The value name an earlier build wrote. Left in place it would point at an
    /// executable that no longer exists and would show the old name in Task
    /// Manager, so every write and delete sweeps it.
    /// </summary>
    private static string LegacyValueName => "WeChatBridge.Windows" + AppLayout.Current.CoordinationSuffix;

    private static string Command =>
        $"\"{Process.GetCurrentProcess().MainModule?.FileName}\" --background";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;
            if (enabled)
            {
                if (key.GetValue(ValueName) is not string current || current != Command)
                    key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            // Whatever the toggle says, the previous build's entry must go: it
            // names an executable this build does not ship.
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // A policy-denied Run key leaves the feature off — the share still
            // works, just slower. The settings toggle reads the registry next
            // open, so a failed write self-corrects visually too.
        }
    }
}
