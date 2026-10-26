using System.Diagnostics;
using System.IO;
using System.Windows;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows;

/// <summary>
/// The one settings dialog. Edits are held until 保存, which validates the vault
/// and the relative folder before touching the running reception service — a
/// typo must fail here, not halfway through an archive.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ReceptionService _reception;

    /// <summary>Working copy of the source override; null means auto-detect.</summary>
    private string? _sourceOverride;

    public SettingsWindow(ReceptionService reception)
    {
        _reception = reception;
        InitializeComponent();

        var settings = reception.Settings;
        VaultBox.Text = settings.ObsidianVaultPath ?? "";
        SubfolderBox.Text = settings.ObsidianSubfolder;
        NotifyBox.IsChecked = settings.NotifyOnArchiveSuccess;
        // Show what the registry actually says: a failed write must not look on.
        LaunchBox.IsChecked = LaunchAtLogin.IsRegistered();
        _sourceOverride = settings.WorkBuddyDirectoryOverride;

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "1.0.8"} · {L10n.Text(Edition.DisplayName)}";
        LogPathText.Text = reception.Paths.Logs;
        LogHintText.Text = L10n.Text("日志记录程序运行状态和错误，可能包含本机路径与异常上下文。分享前请检查导出的压缩包。");
        RefreshSourceSummary();
    }

    /// <summary>
    /// Exports the diagnostic log next to itself and tells the user where it
    /// went. The bundle is the thing a user can actually hand over — the log
    /// file on its own is buried in a folder they never knew existed.
    /// </summary>
    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var bundle = DiagnosticExportService.Export(_reception.Paths, null, out var error);
        if (bundle is null)
        {
            MessageBox.Show(
                this,
                L10n.Format($"导出诊断日志失败：{error}"),
                L10n.Text("律档通"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            DiagnosticSink.Error($"导出诊断日志失败：{error}");
            return;
        }

        DiagnosticSink.Info($"已导出诊断日志：{bundle}");
        var answer = MessageBox.Show(
            this,
            L10n.Format($"诊断日志已导出到：\n{bundle}\n\n是否打开所在文件夹？"),
            L10n.Text("律档通"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            RevealInExplorer(bundle);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) =>
        RevealInExplorer(_reception.Paths.Logs);

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticSink.Warn("打开日志目录失败。", error);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.Text("选择 Obsidian 知识库"),
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            VaultBox.Text = dialog.FolderName;
            VaultError.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshSourceSummary()
    {
        var probe = new WorkBuddySourceProbe().Probe(_sourceOverride);
        SourcePathText.Text = probe.ResolvedPath
            + (probe.IsOverride ? L10n.Text("（手动指定）") : L10n.Text("（自动检测）"));
        SourceStateText.Text = probe.State switch
        {
            WorkBuddySourceState.Ready => L10n.Text("目录可读，可以接收。"),
            WorkBuddySourceState.Missing => L10n.Text("目录尚未出现，将等待它生成。"),
            WorkBuddySourceState.AccessDenied => L10n.Text("没有权限读取该目录。"),
            WorkBuddySourceState.WatchUnavailable => L10n.Text("目录暂时无法监听。"),
            _ => L10n.Text("正在检查。"),
        };
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => RefreshSourceSummary();

    private void ManualSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.Text("选择 WorkBuddy 聊天记录目录"),
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _sourceOverride = dialog.FolderName;
            RefreshSourceSummary();
        }
    }

    private void ResetSource_Click(object sender, RoutedEventArgs e)
    {
        _sourceOverride = null;
        RefreshSourceSummary();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        VaultError.Visibility = Visibility.Collapsed;
        SubfolderError.Visibility = Visibility.Collapsed;

        var vault = VaultBox.Text.Trim();
        if (string.IsNullOrEmpty(vault))
        {
            ShowError(VaultError, L10n.Text("请选择 Obsidian 知识库。"));
            return;
        }
        var (ok, detail) = DestinationProbe.Check(vault);
        if (!ok)
        {
            ShowError(VaultError, detail ?? L10n.Text("无法写入该知识库。"));
            return;
        }

        var subfolder = SubfolderBox.Text.Trim();
        if (!IsValidSubfolder(subfolder, out var reason))
        {
            ShowError(SubfolderError, reason);
            return;
        }

        var launch = LaunchBox.IsChecked == true;
        LaunchAtLogin.Apply(launch);

        var settings = _reception.Settings;
        settings.ObsidianVaultPath = vault;
        settings.ObsidianSubfolder = string.IsNullOrEmpty(subfolder) ? "微信聊天" : subfolder;
        settings.WorkBuddyDirectoryOverride = string.IsNullOrWhiteSpace(_sourceOverride) ? null : _sourceOverride;
        settings.NotifyOnArchiveSuccess = NotifyBox.IsChecked == true;
        settings.LaunchAtLogin = launch;
        _reception.ApplySettings(settings);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool IsValidSubfolder(string value, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (Path.IsPathRooted(value))
        {
            reason = L10n.Text("请填写知识库内的相对路径，不要使用绝对路径。");
            return false;
        }
        var segments = value.Split('/', '\\');
        if (segments.Any(segment => segment == ".."))
        {
            reason = L10n.Text("保存文件夹不能包含 ..。");
            return false;
        }
        var invalid = Path.GetInvalidFileNameChars();
        if (segments.Any(segment => segment.Length > 0 && segment.IndexOfAny(invalid) >= 0))
        {
            reason = L10n.Text("保存文件夹包含不允许的字符。");
            return false;
        }
        return true;
    }

    private static void ShowError(System.Windows.Controls.TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }
}
