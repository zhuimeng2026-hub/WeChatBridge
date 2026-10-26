using System.Windows;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows;

/// <summary>
/// The minimal desktop window. It confirms three things only — whether reception
/// is running, where records are saved, and what happened recently — and offers
/// exactly one repair action per state. Everything else lives behind 设置.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ReceptionService _reception;

    public MainWindow(ReceptionService reception)
    {
        _reception = reception;
        InitializeComponent();
        DataContext = _reception;

        // Closing hides to the tray: the resident process keeps receiving, and
        // the tray's 退出 is the only thing that ends it.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Reload the ledger — called when the shell signals a change.</summary>
    public void RefreshBatches() => _reception.RefreshFromDisk();

    /// <summary>Second launch foregrounds the existing window.</summary>
    public void BringToFront()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        // Activate alone cannot steal focus from another app; the Topmost flick can.
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var window = new SettingsWindow(_reception) { Owner = this };
        window.ShowDialog();
    }

    private void StatusAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_reception.StatusAction)
        {
            case StatusActionKind.ChooseVault:
                ChooseVault();
                break;
            case StatusActionKind.Pause:
                _reception.PauseReception();
                break;
            case StatusActionKind.Resume:
                _reception.ResumeReception();
                break;
            case StatusActionKind.Recheck:
                _reception.RecheckNow();
                break;
            case StatusActionKind.CheckDestination:
                OpenSettings();
                break;
        }
    }

    private void ChooseVault()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.Text("选择 Obsidian 知识库"),
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            _reception.SetVault(dialog.FolderName, _reception.Settings.ObsidianSubfolder);
    }

    private void OpenVault_Click(object sender, RoutedEventArgs e)
    {
        // Nothing configured yet: take the user straight to Settings so they
        // can pick a vault, rather than popping a "please choose" notice that
        // leaves them hunting for the right window.
        if (!_reception.Settings.IsVaultConfigured)
            OpenSettings();
        else
            _reception.OpenVault();
    }

    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReceptionRow row })
            return;
        switch (row.Job.State)
        {
            case ReceptionJobState.Failed:
                _reception.Retry(row);
                break;
            case ReceptionJobState.WaitingForSource:
                _reception.RecheckNow();
                break;
            default:
                _reception.OpenNote(row);
                break;
        }
    }
}
