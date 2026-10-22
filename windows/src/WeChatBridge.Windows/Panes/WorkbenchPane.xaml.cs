using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Panes;

public partial class WorkbenchPane : UserControl
{
    private readonly MainViewModel _model;
    private readonly ObservableCollection<MaterialRow> _visibleMaterials = [];
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private ObsidianVaultWorkbench? _vault;
    private IReadOnlyList<string> _markdownPaths = [];
    private string? _selectedPath;
    private string? _loadedVaultPath;
    private bool _hasLoadedVault;
    private int _refreshGeneration;
    private IReadOnlyList<RequestSource> _lastRequestSources = [];
    private string? _lastRequestPrompt;
    private DateTimeOffset? _lastRequestAt;
    private bool _assistantCollapsed;

    public WorkbenchPane(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        MaterialsList.ItemsSource = _visibleMaterials;
        VaultLabel.Text = L10n.Text("本地 Obsidian 知识库");
        RefreshButton.Content = L10n.Text("刷新");
        ChooseVaultButton.Content = L10n.Text("选择知识库");
        SearchBox.ToolTip = L10n.Text("搜索笔记");
        DocumentTitle.Text = L10n.Text("选择一篇 Markdown 笔记开始阅读");
        AssistantTitle.Text = L10n.Text("助手工作区");
        AssistantNotice.Text = L10n.Text("离线工作台 · 不会自动调用模型或发送材料");
        PromptBox.ToolTip = L10n.Text("写下你的问题或整理要求");
        DraftTitleBox.ToolTip = L10n.Text("新笔记标题");
        ResultBox.ToolTip = L10n.Text("在此粘贴或编辑你的分析结果");
        PromptHint.Text = L10n.Text("写下整理要求。复制材料后，由你粘贴到已选择的工具；再把结果粘贴回右侧。工作台不会自动发送材料。");
        ResultHint.Text = L10n.Text("在此粘贴或编辑结果。微信流不会把模板或示例标成 AI 结论。");
        DraftTitleBox.Text = L10n.Text("新建分析草稿");
        SaveFolderBox.Text = "分析";
        SaveFolderBox.ToolTip = L10n.Text("在知识库内的保存文件夹");
        SaveFolderLabel.Text = L10n.Text("保存到");
        CollapseButton.Content = L10n.Text("收起");
        UpdateHints();
        CopyRequestButton.Content = L10n.Text("复制材料与提示词");
        SaveNoteButton.Content = L10n.Text("保存为新笔记");
        Loaded += (_, _) => Activate();
    }

    /// <summary>Tests and shell navigation can request a refresh after deferred pane loading.</summary>
    public async void Activate()
    {
        if (_hasLoadedVault && string.Equals(_loadedVaultPath, _model.ObsidianVaultPath, StringComparison.OrdinalIgnoreCase)) return;
        await RefreshVaultAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshVaultAsync();

    private async void ChooseVault_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L10n.Text("选择 Obsidian 知识库文件夹"),
            InitialDirectory = _model.ObsidianVaultPath is { Length: > 0 } path && Directory.Exists(path) ? path : string.Empty,
        };
        if (dialog.ShowDialog() != true) return;
        _model.ObsidianVaultPath = dialog.FolderName;
        await RefreshVaultAsync();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        try { Refilter(); }
        catch (Exception error) { StatusText.Text = L10n.Format($"搜索失败：{error.Message}"); }
    }

    private void Prompt_TextChanged(object sender, TextChangedEventArgs e) => UpdateHints();
    private void Result_TextChanged(object sender, TextChangedEventArgs e) => UpdateHints();

    private void UpdateHints()
    {
        if (PromptHint is not null) PromptHint.Visibility = string.IsNullOrEmpty(PromptBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (ResultHint is not null) ResultHint.Visibility = string.IsNullOrEmpty(ResultBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Materials_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaterialsList.SelectedItem is not MaterialRow row) return;
        _selectedPath = null;
        DocumentTitle.Text = L10n.Text("选择一篇 Markdown 笔记开始阅读");
        DocumentPath.Text = "";
        DocumentBody.Clear();
        AttachmentsList.ItemsSource = null;
        try
        {
            var document = _vault?.ReadMarkdown(row.RelativePath) ?? "";
            _selectedPath = row.RelativePath;
            DocumentTitle.Text = row.Name;
            DocumentPath.Text = row.RelativePath;
            DocumentBody.Text = document;
            AttachmentsList.ItemsSource = _vault?.ListAttachments(row.RelativePath);
            if (string.IsNullOrWhiteSpace(DraftTitleBox.Text) || DraftTitleBox.Text == L10n.Text("新建分析草稿"))
                DraftTitleBox.Text = row.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? row.Name[..^3] + "分析草稿"
                    : row.Name + "分析草稿";
            StatusText.Text = L10n.Text("原文只读 · 勾选材料后可复制到剪贴板");
            RefreshSourceLinks();
        }
        catch (Exception error)
        {
            _selectedPath = null;
            DocumentBody.Clear();
            AttachmentsList.ItemsSource = null;
            StatusText.Text = L10n.Format($"读取失败：{error.Message}");
        }
    }

    private void CopyRequest_Click(object sender, RoutedEventArgs e)
    {
        var passage = DocumentBody.SelectedText.Trim();
        var selectedPaths = _selectedPaths.ToList();
        if (passage.Length > 0 && _selectedPath is not null && !selectedPaths.Contains(_selectedPath, StringComparer.OrdinalIgnoreCase))
            selectedPaths.Add(_selectedPath);
        if (selectedPaths.Count == 0)
        {
            StatusText.Text = L10n.Text("请先勾选至少一份材料");
            return;
        }
        try
        {
            var sections = selectedPaths.Select(path => path == _selectedPath && passage.Length > 0
                ? $"## 来源：{path} · 当前选中段落\n\n> {passage.Replace("\n", "\n> ", StringComparison.Ordinal)}"
                : $"## 来源：{path}\n\n{_vault!.ReadMarkdown(path)}");
            var prompt = PromptBox.Text.Trim();
            var sectionArray = sections.ToArray();
            var request = string.IsNullOrWhiteSpace(prompt)
                ? string.Join("\n\n---\n\n", sectionArray)
                : $"{prompt}\n\n" + string.Join("\n\n---\n\n", sectionArray);
            Clipboard.SetText(request);
            _lastRequestSources = selectedPaths.Select(path => new RequestSource(
                path, path == _selectedPath && passage.Length > 0 ? passage : null)).ToArray();
            _lastRequestPrompt = prompt;
            _lastRequestAt = DateTimeOffset.Now;
            StatusText.Text = passage.Length > 0
                ? L10n.Format($"已复制 {selectedPaths.Count} 份材料，含当前选中段落；请自行粘贴到所选工具")
                : L10n.Format($"已复制 {selectedPaths.Count} 份材料；请自行粘贴到所选工具");
        }
        catch (Exception error)
        {
            StatusText.Text = L10n.Format($"复制失败：{error.Message}");
        }
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (_vault is null)
        {
            StatusText.Text = L10n.Text("请先选择 Obsidian 知识库");
            return;
        }
        if (string.IsNullOrWhiteSpace(ResultBox.Text))
        {
            StatusText.Text = L10n.Text("请先粘贴或填写分析结果");
            return;
        }
        try
        {
            var requestSources = _lastRequestAt is null ? CurrentRequestSources() : _lastRequestSources;
            var sources = requestSources.Select(source => $"- [[{source.Path}]]"
                + (source.Passage is null ? "" : $"\n  > 选中段落：{source.Passage.Replace("\n", "\n  > ", StringComparison.Ordinal)}")).ToArray();
            var result = ResultBox.Text.Trim();
            var markdown = $"# {DraftTitleBox.Text.Trim()}\n\n> 本笔记由工作台新建。结果由用户粘贴或编辑；微信流未调用模型。\n\n"
                + "## 分析结果\n\n" + result + "\n\n"
                + "## 请求记录\n\n"
                + (_lastRequestAt is null
                    ? "本草稿由用户手动整理；没有从工作台复制请求。\n"
                    : $"复制到剪贴板时间：{_lastRequestAt:yyyy-MM-dd HH:mm:ss zzz}\n\n提示词：\n\n{_lastRequestPrompt}\n")
                + "\n## 参考材料\n\n" + (sources.Length == 0 ? "- （未选择材料）" : string.Join("\n", sources)) + "\n";
            var destination = new ObsidianVaultWorkbench(_model.ObsidianVaultPath!, SaveFolderBox.Text.Trim());
            var savedPath = destination.SaveNewAnalysis(DraftTitleBox.Text, markdown);
            StatusText.Text = L10n.Format($"已新建笔记：{savedPath}");
        }
        catch (Exception error)
        {
            StatusText.Text = L10n.Format($"保存失败：{error.Message}");
        }
    }

    private async Task RefreshVaultAsync()
    {
        var generation = ++_refreshGeneration;
        _hasLoadedVault = false;
        _selectedPath = null;
        _selectedPaths.Clear();
        _lastRequestSources = [];
        _lastRequestPrompt = null;
        _lastRequestAt = null;
        _markdownPaths = [];
        _visibleMaterials.Clear();
        DocumentBody.Clear();
        DocumentPath.Text = "";
        AttachmentsList.ItemsSource = null;
        SourceLinks.ItemsSource = null;
        DocumentTitle.Text = L10n.Text("选择一篇 Markdown 笔记开始阅读");
        var root = _model.ObsidianVaultPath;
        VaultPath.Text = string.IsNullOrWhiteSpace(root) ? L10n.Text("尚未设置知识库") : root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _vault = null;
            _loadedVaultPath = root;
            _hasLoadedVault = true;
            SelectionCount.Text = L10n.Text("请选择本地知识库");
            StatusText.Text = L10n.Text("工作台仅读取本地 Markdown 笔记");
            return;
        }
        try
        {
            var vault = new ObsidianVaultWorkbench(root);
            _vault = vault;
            StatusText.Text = L10n.Text("正在读取本地 Markdown 笔记…");
            var paths = await Task.Run(vault.ListMarkdownFiles);
            if (generation != _refreshGeneration || !string.Equals(root, _model.ObsidianVaultPath, StringComparison.OrdinalIgnoreCase)) return;
            _markdownPaths = paths;
            _loadedVaultPath = root;
            _hasLoadedVault = true;
            Refilter();
            StatusText.Text = L10n.Format($"已连接 · {_markdownPaths.Count} 篇 Markdown 笔记");
        }
        catch (Exception error)
        {
            if (generation != _refreshGeneration) return;
            _vault = null;
            StatusText.Text = L10n.Format($"无法打开知识库：{error.Message}");
        }
    }

    private void Refilter()
    {
        if (_vault is null) return;
        var previousPath = _selectedPath;
        var query = SearchBox.Text.Trim();
        _visibleMaterials.Clear();
        foreach (var path in _markdownPaths.Where(path => query.Length == 0
                     || Path.GetFileName(path).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || path.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            var row = new MaterialRow(path, Path.GetFileName(path)) { IsSelected = _selectedPaths.Contains(path) };
            row.SelectionChanged = () => SetSelected(path);
            _visibleMaterials.Add(row);
        }
        if (previousPath is not null)
        {
            var selected = _visibleMaterials.FirstOrDefault(item => item.RelativePath == previousPath);
            if (selected is not null) MaterialsList.SelectedItem = selected;
        }
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        var count = _selectedPaths.Count;
        SelectionCount.Text = L10n.Format($"已选 {count} 份材料 · 可加入请求");
        RefreshSourceLinks();
    }

    private void SetSelected(string path)
    {
        var row = _visibleMaterials.FirstOrDefault(item => item.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (row?.IsSelected == true) _selectedPaths.Add(path);
        else _selectedPaths.Remove(path);
        UpdateSelectionCount();
    }

    private void RefreshSourceLinks()
    {
        if (SourceLinks is null) return;
        SourceLinks.ItemsSource = _selectedPaths.Select(path => new MaterialRow(path, Path.GetFileName(path))).ToArray();
    }

    private void SourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        SearchBox.Clear();
        var row = _visibleMaterials.FirstOrDefault(item => item.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (row is not null) MaterialsList.SelectedItem = row;
        DocumentBody.Focus();
    }

    private IReadOnlyList<RequestSource> CurrentRequestSources()
    {
        var paths = _selectedPaths.ToList();
        var passage = DocumentBody.SelectedText.Trim();
        if (passage.Length > 0 && _selectedPath is not null && !paths.Contains(_selectedPath, StringComparer.OrdinalIgnoreCase))
            paths.Add(_selectedPath);
        return paths.Select(path => new RequestSource(path, path == _selectedPath && passage.Length > 0 ? passage : null)).ToArray();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _assistantCollapsed = !_assistantCollapsed;
        AssistantHeight.Height = _assistantCollapsed ? new GridLength(48) : new GridLength(250);
        AssistantBody.Visibility = _assistantCollapsed ? Visibility.Collapsed : Visibility.Visible;
        AssistantFooter.Visibility = _assistantCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _assistantCollapsed ? L10n.Text("展开") : L10n.Text("收起");
    }

    private sealed class MaterialRow(string relativePath, string name)
    {
        private bool _isSelected;
        public string RelativePath { get; } = relativePath;
        public string Name { get; } = name;
        public Action? SelectionChanged { get; set; }
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; SelectionChanged?.Invoke(); }
        }
    }

    private sealed record RequestSource(string Path, string? Passage);
}
