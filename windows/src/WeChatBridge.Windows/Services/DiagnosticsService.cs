using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Writes a snapshot of the environment the product is running in.
/// <para>
/// A log line saying "opening the vault failed" is close to useless on a
/// machine nobody has seen. What makes it useful is the context around it:
/// which Obsidian, which vault registry, which source folder, which data
/// directory, and whether the process is even the one that should be running.
/// The snapshot records those facts once, at startup, so every later line can
/// be read against them.
/// </para>
/// <para>
/// What it deliberately does not record: chat content, note bodies, account
/// identifiers, licence keys, or any token. Only the shape of the environment
/// — paths, versions, booleans — is written. A diagnostic log that can leak
/// what the user archived is one nobody will be willing to send.
/// </para>
/// </summary>
internal static class DiagnosticsService
{
    /// <summary>
    /// Writes the snapshot. Every probe is individually guarded: a fact that
    /// cannot be determined is reported as such rather than aborting the
    /// snapshot, because a partial picture is still worth having.
    /// </summary>
    public static void WriteSnapshot(InboxPaths paths, AppSettings settings)
    {
        try
        {
            var layout = AppLayout.Current;
            var lines = new List<string>
            {
                "=== 运行环境快照 ===",
                $"产品版本: {ProductVersion()}",
                $"运行时: {Environment.Version} | {RuntimeInformation.RuntimeIdentifier}"
                    + $" | OS: {RuntimeInformation.OSDescription.Trim()}",
                $"进程架构: {RuntimeInformation.ProcessArchitecture} | 操作系统架构: {RuntimeInformation.OSArchitecture}",
                $"安装形态: {(layout.IsPortable ? "便携版" : "安装版")}",
                $"可执行文件: {SafePath(() => Environment.ProcessPath)}",
                $"数据目录: {SafePath(() => layout.DataRoot)}",
                $"Inbox 目录: {SafePath(() => paths.Root)}",
                $"协调后缀: {SafePath(() => layout.CoordinationSuffix)}",
                $"当前用户: {SafeText(() => WindowsIdentity.GetCurrent().Name)}",
                $"管理员权限: {SafeText(() => IsAdministrator().ToString())}",
                $"本地化: {SafeText(() => CultureInfo.CurrentCulture.Name)}"
                    + $" | UI 语言: {SafeText(() => settings.Language)}",
                $"时区: {SafeText(() => TimeZoneInfo.Local.Id)}",
                $"配置: schemaVersion={settings.SchemaVersion}"
                    + $" | 知识库={SafePath(() => settings.ObsidianVaultPath)}"
                    + $" | 子目录={SafeText(() => settings.ObsidianSubfolder)}"
                    + $" | 接收已暂停={SafeText(() => settings.ReceptionPaused.ToString())}",
            };

            lines.AddRange(ObsidianFacts());
            lines.AddRange(WorkBuddyFacts(settings));
            lines.AddRange(VaultFacts(settings));
            lines.Add("=== 快照结束 ===");

            foreach (var line in lines)
                DiagnosticLog.Info(paths, line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidOperationException)
        {
            DiagnosticLog.Warn(paths, "写入环境快照失败", error);
        }
    }

    /// <summary>
    /// What Obsidian is present, and what it knows about. The vault registry
    /// is the part that decides whether an <c>obsidian://</c> URI can work at
    /// all, so it is worth having in the log.
    /// </summary>
    private static IEnumerable<string> ObsidianFacts()
    {
        yield return "--- Obsidian ---";
        yield return $"  协议已注册: {SafeText(() => ObsidianProtocol.IsRegistered().ToString())}";
        yield return $"  便携版自带: {SafePath(() => ObsidianProtocol.PortableExecutable)}";
        yield return $"  正在运行: {SafeText(() => ObsidianVaultRegistry.IsRunning().ToString())}";
        var vaults = SafeList(() => ObsidianVaultRegistry.List());
        if (vaults is null || vaults.Count == 0)
        {
            yield return "  已注册知识库: (无)";
        }
        else
        {
            foreach (var vault in vaults)
                yield return $"  已注册知识库: id={vault.Id} path={vault.Path}"
                    + $" exists={SafeText(() => Directory.Exists(vault.Path).ToString())}";
        }
    }

    /// <summary>
    /// The source folder the archives arrive in. Whether it exists, and
    /// whether it is readable, decides whether anything arrives at all.
    /// </summary>
    private static IEnumerable<string> WorkBuddyFacts(AppSettings settings)
    {
        yield return "--- WorkBuddy 来源 ---";
        var resolved = SafeText(() => WorkBuddySourceResolver.Resolve(settings.WorkBuddyDirectoryOverride).Path);
        yield return $"  来源目录: {resolved}";
        yield return $"  目录存在: {SafeText(() => Directory.Exists(resolved).ToString())}";
    }

    /// <summary>
    /// The destination the archives are written to, and whether it can be
    /// written to at all.
    /// </summary>
    private static IEnumerable<string> VaultFacts(AppSettings settings)
    {
        yield return "--- 知识库目标 ---";
        var vault = settings.ObsidianVaultPath;
        if (string.IsNullOrWhiteSpace(vault))
        {
            yield return "  (未配置)";
            yield break;
        }
        yield return $"  路径: {vault}";
        yield return $"  存在: {SafeText(() => Directory.Exists(vault).ToString())}";
        yield return $"  是 Obsidian 知识库: {SafeText(() => Directory.Exists(Path.Combine(vault, ".obsidian")).ToString())}";
        yield return $"  已被 Obsidian 注册: {SafeText(() => ObsidianVaultRegistry.IsRegistered(vault).ToString())}";
    }

    private static string ProductVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                ?? assembly.GetName().Version?.ToString()
                ?? "(未知)";
            return version;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            return "(未知)";
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a probe and returns its value, or a marker when it could not be
    /// determined. A probe that throws tells us the fact just as well as a
    /// probe that returns "no" — and it must not abort the snapshot.
    /// </summary>
    private static string SafeText(Func<string?> probe)
    {
        try
        {
            return probe() ?? "(null)";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidOperationException or System.Security.SecurityException)
        {
            return "(无法读取: " + error.GetType().Name + ")";
        }
    }

    private static string SafePath(Func<string?> probe) => SafeText(probe);

    private static IReadOnlyList<ObsidianVaultEntry>? SafeList(Func<IReadOnlyList<ObsidianVaultEntry>> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
