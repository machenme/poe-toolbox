using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibBundle3.Nodes;
using LibBundle3.Records;
using Microsoft.Win32;
using PoEToolbox.Sdk;
using PoEToolbox.Plugins.DataBrowser.Models;
using PoEToolbox.Plugins.DataBrowser.Services;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser;

public partial class DataBrowserView : UserControl
{
    private GameDataAccess? _gd;
    private IDirectoryNode? _rootNode;
    private DataBrowserCacheService.CachedTreeIndex? _cachedTree;
    private ObservableCollection<TreeItemViewModel>? _allItems;
    private TreeItemViewModel? _currentDir;
    private readonly List<FileItemViewModel> _allFileItems = [];
    private readonly DataBrowserConfig _config;
    private readonly IEventBus _eventBus;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _operationCts;
    private FileItemViewModel? _selectedFile;
    private PreviewDocument? _currentPreview;
    private bool _isEditingText;
    private string? _editingPath;
    private string? _editingOriginalText;
    private readonly Dictionary<string, PendingTextEdit> _pendingEdits = new(StringComparer.OrdinalIgnoreCase);
    private string? _reopenPath;

    public DataBrowserView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        InitializeComponent();
        _config = ConfigService.GetPluginConfig<DataBrowserConfig>("DataBrowser")
                  ?? new DataBrowserConfig();
        ExtensionFilter.SelectedIndex = 0;
        TextPreview.FontSize = Math.Clamp(_config.EditorFontSize, 8, 32);
    }

    public void ReleaseFileLocks(bool keepReopenPath = false)
    {
        if (keepReopenPath)
            _reopenPath ??= _gd?.GameDataPath;
        else
            _reopenPath = null;

        _operationCts?.Cancel();
        _operationCts = null;
        _previewCts?.Cancel();
        _previewCts = null;
        _searchCts?.Cancel();
        _searchCts = null;

        _gd?.Dispose();
        _gd = null;
        _rootNode = null;
        _cachedTree = null;
        _allItems = null;
        _currentDir = null;
        _selectedFile = null;
        _allFileItems.Clear();

        DirTree.ItemsSource = null;
        FileList.ItemsSource = null;
        PathLabel.Text = keepReopenPath ? _reopenPath ?? string.Empty : string.Empty;
        FileCountLabel.Text = string.Empty;
        FileListPlaceholder.Text = keepReopenPath
            ? "索引已释放，点击“重新打开”以继续浏览"
            : "打开游戏数据文件开始浏览";
        FileListPlaceholder.Visibility = Visibility.Visible;
        ClearPreview();
        SetBusy(false, "已释放游戏数据文件占用", false);
        UpdateFileLockButton();
    }

    // ── Open ────────────────────────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = _config.LastDataPath is not null
            ? (File.Exists(_config.LastDataPath)
                ? Path.GetDirectoryName(_config.LastDataPath)
                : _config.LastDataPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var dlg = new OpenFileDialog
        {
            Filter = "游戏数据文件|*.ggpk;*.index.bin|Content.ggpk|*.ggpk|Index|*.index.bin|All|*.*",
            Title = "选择 Content.ggpk 或 _.index.bin",
            InitialDirectory = Directory.Exists(initialDirectory)
                ? initialDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        if (dlg.ShowDialog() == true)
            OpenPath(dlg.FileName);
    }

    private void FileLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gd is not null)
        {
            ReleaseFileLocks(keepReopenPath: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_reopenPath))
            OpenPath(_reopenPath);
    }

    private async void OpenPath(string path)
    {
        _operationCts?.Cancel();
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        GameDataAccess? opened = null;
        DataSourceSignature? cacheSignature = null;

        try
        {
            SetBusy(true, "正在打开...", true);
            ClearPreview();
            opened = await Task.Run(() => GameDataAccess.OpenReadOnlyMapped(path), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var skippedFileCount = opened.Index.Files.Values.Count(file => string.IsNullOrEmpty(file.Path));
            var browsableFileCount = opened.Index.Files.Count - skippedFileCount;
            if (DataBrowserCacheService.TryCreateSignature(path, out var signature))
                cacheSignature = signature;

            var cachedTree = cacheSignature is null
                ? null
                : await Task.Run(
                    () => DataBrowserCacheService.TryLoadTreeCache(cacheSignature, browsableFileCount),
                    cts.Token);

            IDirectoryNode? root = null;
            ObservableCollection<TreeItemViewModel> items;
            if (cachedTree is not null
                && cachedTree.TryBuildRoot(opened.Index.Files, out var cachedItems))
            {
                items = cachedItems;
            }
            else
            {
                cachedTree = null;
                root = await Task.Run(() => opened.Index.BuildTree(ignoreNullPath: true), cts.Token);
                items = TreeBuilderService.BuildRoot(root);
            }
            cts.Token.ThrowIfCancellationRequested();

            _gd?.Dispose();
            _gd = opened;
            opened = null;
            _reopenPath = _gd.GameDataPath;
            _eventBus.Publish(new GameContextChanged(
                _gd.IsPoe2Client ? PoeGameKind.Poe2 : PoeGameKind.Poe1,
                _gd.GameDataPath,
                PoeDetector.Default.IsPoeRunning()));
            _rootNode = root;
            _cachedTree = cachedTree;
            _allItems = items;
            DirTree.ItemsSource = _allItems;
            PathLabel.Text = path;
            FileCountLabel.Text = $"{_gd.Index.Files.Count:N0} 个文件";
            StatusText.Text = $"已打开 ({(_gd.IsBundles2 ? "Bundles2" : "GGPK")})"
                + (cachedTree is not null ? " · 缓存" : "")
                + (skippedFileCount > 0 ? $" · 已跳过 {skippedFileCount:N0} 个无路径记录" : "");
            FileListPlaceholder.Visibility = Visibility.Visible;
            FileListPlaceholder.Text = "打开游戏数据文件开始浏览";
            _allFileItems.Clear();
            FileList.ItemsSource = null;
            _currentDir = null;

            if (cacheSignature is not null)
            {
                if (cachedTree is null && root is not null)
                    _ = Task.Run(() => DataBrowserCacheService.SaveTreeCache(cacheSignature, root));
            }

            _config.LastDataPath = path;
            ConfigService.SavePluginConfig("DataBrowser", _config);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消打开";
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开失败";
            MessageBox.Show($"无法打开:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            opened?.Dispose();
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
                SetBusy(false, StatusText.Text, false);
            }
            cts.Dispose();
            UpdateFileLockButton();
        }
    }

    // ── TreeView ────────────────────────────────────────

    private void DirTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is TreeItemViewModel item && item.IsDirectory)
            LoadDirChildren(item);
    }

    private async void DirTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeItemViewModel item)
            return;

        if (item.IsDirectory)
        {
            ShowFilesForDirectory(item);
            return;
        }

        if (item.FileRecord is not null)
            await PreviewFileAsync(ToFileItem(item));
    }

    private void LoadDirChildren(TreeItemViewModel item)
    {
        if (item.IsLoaded || _gd is null) return;
        if (_cachedTree is not null)
        {
            _cachedTree.LoadChildren(item, _gd.Index.Files);
            return;
        }

        if (_rootNode == null) return;
        var dirNode = FindDirNode(_rootNode, item.FullPath);
        if (dirNode is not null)
            TreeBuilderService.LoadChildren(item, dirNode);
    }

    private void ShowFilesForDirectory(TreeItemViewModel dirItem)
    {
        if (!dirItem.IsDirectory) return;
        LoadDirChildren(dirItem);

        _allFileItems.Clear();
        if (dirItem.Children is not null)
        {
            foreach (var child in dirItem.Children)
            {
                if (!child.IsDirectory && child != TreeItemViewModel.Dummy)
                    _allFileItems.Add(ToFileItem(child));
            }
        }

        _currentDir = dirItem;
        ApplySearchFilter();
        FileList.Tag = dirItem;
        FileListPlaceholder.Visibility = _allFileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchStatusText.Text = $"{_allFileItems.Count:N0} 个文件";
    }

    private static IDirectoryNode? FindDirNode(IDirectoryNode root, string path)
    {
        if (string.IsNullOrEmpty(path)) return root;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IDirectoryNode current = root;
        foreach (var part in parts)
        {
            var found = current.Children.OfType<IDirectoryNode>()
                .FirstOrDefault(d => string.Equals(d.Name, part, StringComparison.Ordinal));
            if (found is null) return null;
            current = found;
        }
        return current;
    }

    // ── Search ──────────────────────────────────────────

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var cancellationToken = cts.Token;
        try
        {
            await Task.Delay(200, cancellationToken);
            await DoSearchAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
                _searchCts = null;
            cts.Dispose();
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _searchCts?.Cancel();
            _ = DoSearchAsync(CancellationToken.None);
            e.Handled = true;
        }
    }

    private void ExtensionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            _ = DoSearchAsync(CancellationToken.None);
    }

    private async Task DoSearchAsync(CancellationToken cancellationToken)
    {
        var filter = SearchBox.Text.Trim();
        var extension = GetSelectedExtension();
        if (extension == "全部") extension = null;

        if (filter.Length == 0 && extension is null)
        {
            ApplySearchFilter();
            FileList.Tag = _currentDir;
            SearchStatusText.Text = $"{_allFileItems.Count:N0} 个文件";
            FileListPlaceholder.Visibility = _allFileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (_gd is null)
        {
            SearchStatusText.Text = "未打开数据源";
            return;
        }

        var data = _gd;
        StatusText.Text = $"搜索中: {filter}...";
        var result = await Task.Run(() =>
        {
            var list = new List<FileItemViewModel>();
            var truncated = false;
            foreach (var file in data.Index.Files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = file.Path;
                if (string.IsNullOrEmpty(path)) continue;
                if (filter.Length > 0 && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (extension is not null && !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;

                if (list.Count == 500)
                {
                    truncated = true;
                    break;
                }
                list.Add(new FileItemViewModel
                {
                    Name = Path.GetFileName(path),
                    FullPath = path,
                    SizeDisplay = FormatSize(file.Size),
                    FileRecord = file,
                });
            }
            return (Items: list.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToList(), Truncated: truncated);
        }, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;
        FileList.ItemsSource = result.Items;
        FileList.Tag = null;
        FileListPlaceholder.Visibility = result.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchStatusText.Text = $"{result.Items.Count:N0} 个结果" + (result.Truncated ? "（仅显示前 500 项）" : "");
        StatusText.Text = "就绪";
    }

    private void ApplySearchFilter()
    {
        var filter = SearchBox.Text.Trim();
        var extension = GetSelectedExtension();
        if (extension == "全部") extension = null;

        FileList.ItemsSource = _allFileItems
            .Where(file => filter.Length == 0 || file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(file => extension is null || Path.GetExtension(file.FullPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string? GetSelectedExtension()
    {
        var value = (ExtensionFilter.SelectedItem as ComboBoxItem)?.Content as string;
        return value == "全部" ? null : value;
    }

    // ── Preview ─────────────────────────────────────────

    private static FileItemViewModel ToFileItem(TreeItemViewModel item) => new()
    {
        Name = item.Name,
        FullPath = item.FullPath,
        SizeDisplay = FormatSize(item.Size),
        FileRecord = item.FileRecord,
    };

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel file)
            await PreviewFileAsync(file);
    }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel file)
            await PreviewFileAsync(file);
    }

    private async void PreviewFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel file)
            await PreviewFileAsync(file);
    }

    private async void ReloadPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is not null)
            await PreviewFileAsync(_selectedFile);
    }

    private void EditFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile?.FileRecord is null
            || _currentPreview?.Text is not string text
            || !IsEditablePreview(_currentPreview)
            || _currentPreview.IsTruncated)
            return;

        _isEditingText = true;
        _editingPath = _selectedFile.FullPath;
        _editingOriginalText = text;
        TextPreview.IsReadOnly = false;
        TextPreview.Text = text;
        TextPreview.Visibility = Visibility.Visible;
        JsonPreview.Visibility = Visibility.Collapsed;
        EditFileButton.Visibility = Visibility.Collapsed;
        SaveFileButton.Visibility = Visibility.Visible;
        SaveFileButton.IsEnabled = false;
        CancelEditButton.Visibility = Visibility.Visible;
        ReloadPreviewButton.Visibility = Visibility.Collapsed;
        EditorToolbar.Visibility = Visibility.Visible;
        PreviewPanel.SetValue(Grid.RowProperty, 1);
        PreviewPanel.SetValue(Grid.RowSpanProperty, 2);
        FileListPanel.Visibility = Visibility.Collapsed;
        DirTree.IsEnabled = false;
        FileList.IsEnabled = false;
        OpenFileButton.IsEnabled = false;
        FileLockButton.IsEnabled = false;
        StatusText.Text = "编辑中，保存前会创建原始索引备份";
        TextPreview.Focus();
        TextPreview.CaretIndex = TextPreview.Text.Length;
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditingText)
            return;
        if (!ConfirmDiscardEdit())
            return;

        var preview = _currentPreview;
        StopTextEdit();
        if (preview is not null)
            ApplyPreview(preview);
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditingText || string.IsNullOrWhiteSpace(_editingPath))
            return;

        var virtualPath = _editingPath;
        var content = TextPreview.Text;
        if (string.Equals(content, _editingOriginalText, StringComparison.Ordinal))
            return;

        var encoding = _currentPreview?.TextEncoding ?? new UTF8Encoding(false);
        _pendingEdits[virtualPath] = new PendingTextEdit(virtualPath, _editingOriginalText ?? "", content, encoding);
        StopTextEdit();
        UpdatePendingChangesUi();
        StatusText.Text = $"已暂存修改：{virtualPath}（点击“保存全部修改”写入）";
    }

    private async void SaveAllChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingEdits.Count == 0 || _gd is null) return;
        var confirmation = MessageBox.Show($"将写入 {_pendingEdits.Count} 个文件的修改，并创建原始索引备份。是否继续？", "确认保存全部修改", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        var gameDataPath = _gd.GameDataPath;
        var edits = _pendingEdits.Values.ToList();
        ReleaseFileLocks(keepReopenPath: true);
        var cts = new CancellationTokenSource(); _operationCts = cts;
        SetBusy(true, "正在保存全部修改...", true); ProgressBar.IsIndeterminate = true;
        try
        {
            await Task.Run(() => ReplaceService.ReplaceTexts(gameDataPath, edits, cts.Token), cts.Token);
            _pendingEdits.Clear(); UpdatePendingChangesUi(); StatusText.Text = $"已保存 {edits.Count} 个文件";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = "保存失败"; MessageBox.Show($"保存失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { if (ReferenceEquals(_operationCts, cts)) _operationCts = null; cts.Dispose(); SetBusy(false, StatusText.Text, false); }
        OpenPath(gameDataPath);
    }

    private void ViewChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingEdits.Count == 0) return;
        var summary = string.Join(Environment.NewLine, _pendingEdits.Values.Select(edit => $"{edit.VirtualPath}    {edit.OriginalText.Length:N0} -> {edit.EditedText.Length:N0} chars"));
        MessageBox.Show(summary, "本次待保存改动", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DiscardChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingEdits.Count == 0) return;
        if (MessageBox.Show("撤销本次尚未保存的全部修改？", "确认撤销", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _pendingEdits.Clear();
        UpdatePendingChangesUi();
        StatusText.Text = "已撤销全部待保存修改";
    }

    private void UpdatePendingChangesUi()
    {
        var visible = _pendingEdits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingChangesButton.Visibility = visible; ViewChangesButton.Visibility = visible;
        DiscardChangesButton.Visibility = visible;
        PendingChangesButton.Content = $"保存全部修改 ({_pendingEdits.Count})";
    }

    private void EditorSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext_Click(sender, e);
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditingText || string.IsNullOrEmpty(FindTextBox.Text))
            return;

        var comparison = MatchCaseCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var start = TextPreview.SelectionStart + TextPreview.SelectionLength;
        var index = TextPreview.Text.IndexOf(FindTextBox.Text, start, comparison);
        if (index < 0 && start > 0)
            index = TextPreview.Text.IndexOf(FindTextBox.Text, 0, comparison);

        if (index < 0)
        {
            EditorMatchText.Text = "未找到";
            return;
        }

        TextPreview.Focus();
        TextPreview.Select(index, FindTextBox.Text.Length);
        EditorMatchText.Text = $"位置 {index + 1}";
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditingText || string.IsNullOrEmpty(FindTextBox.Text))
            return;

        var comparison = MatchCaseCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var source = TextPreview.Text;
        var find = FindTextBox.Text;
        var replacement = ReplaceTextBox.Text;
        var builder = new StringBuilder(source.Length);
        var position = 0;
        var count = 0;
        while (position < source.Length)
        {
            var index = source.IndexOf(find, position, comparison);
            if (index < 0)
            {
                builder.Append(source, position, source.Length - position);
                break;
            }

            builder.Append(source, position, index - position);
            builder.Append(replacement);
            position = index + find.Length;
            count++;
        }

        if (count == 0)
        {
            EditorMatchText.Text = "未找到";
            return;
        }

        TextPreview.Text = builder.ToString();
        EditorMatchText.Text = $"已替换 {count:N0} 处";
    }

    private void TextPreview_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isEditingText)
            SaveFileButton.IsEnabled = !string.Equals(
                TextPreview.Text, _editingOriginalText, StringComparison.Ordinal);
    }

    private async Task PreviewFileAsync(FileItemViewModel file)
    {
        if (file.FileRecord is null) return;

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _selectedFile = file;
        SetPreviewLoading(file.FullPath);

        try
        {
            var document = await PreviewService.LoadAsync(file.FileRecord, file.FullPath, cts.Token);
            if (!cts.IsCancellationRequested)
                ApplyPreview(document);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ApplyPreview(new PreviewDocument
            {
                FilePath = file.FullPath,
                Title = file.Name,
                Kind = PreviewKind.Error,
                Summary = "预览失败",
                ErrorMessage = ex.Message,
            });
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
                _previewCts = null;
            cts.Dispose();
        }
    }

    private void SetPreviewLoading(string path)
    {
        StopTextEdit();
        _currentPreview = null;
        PreviewTitle.Text = Path.GetFileName(path);
        PreviewSummary.Text = "读取中...";
        EditFileButton.Visibility = Visibility.Collapsed;
        SaveFileButton.Visibility = Visibility.Collapsed;
        CancelEditButton.Visibility = Visibility.Collapsed;
        EditorToolbar.Visibility = Visibility.Collapsed;
        ReloadPreviewButton.Visibility = Visibility.Collapsed;
        PreviewEmpty.Visibility = Visibility.Collapsed;
        PreviewError.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        JsonPreview.Visibility = Visibility.Collapsed;
        DatPreview.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在读取预览...";
    }

    private void ApplyPreview(PreviewDocument document)
    {
        _currentPreview = document;
        TextPreview.IsReadOnly = true;
        TextPreview.Visibility = Visibility.Collapsed;
        JsonPreview.Visibility = Visibility.Collapsed;
        DatPreview.Visibility = Visibility.Collapsed;
        PreviewEmpty.Visibility = Visibility.Collapsed;
        PreviewError.Visibility = Visibility.Collapsed;
        EditFileButton.Visibility = IsEditablePreview(document) && !document.IsTruncated
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveFileButton.Visibility = Visibility.Collapsed;
        CancelEditButton.Visibility = Visibility.Collapsed;
        EditorToolbar.Visibility = Visibility.Collapsed;
        ReloadPreviewButton.Visibility = Visibility.Visible;
        PreviewTitle.Text = document.Title;
        PreviewSummary.Text = document.Summary;

        switch (document.Kind)
        {
            case PreviewKind.Text:
            case PreviewKind.Hex:
                TextPreview.Text = document.Kind == PreviewKind.Hex ? document.HexText : document.Text;
                TextPreview.Visibility = Visibility.Visible;
                break;
            case PreviewKind.Json:
                if (document.Text is not null)
                {
                    TextPreview.Text = document.Text;
                    TextPreview.Visibility = Visibility.Visible;
                }
                else
                {
                    JsonPreview.ItemsSource = document.JsonRoot;
                    JsonPreview.Visibility = Visibility.Visible;
                }
                break;
            case PreviewKind.DatTable:
                DatPreview.ItemsSource = document.Table?.DefaultView;
                DatPreview.Visibility = Visibility.Visible;
                break;
            case PreviewKind.Error:
                PreviewError.Text = document.ErrorMessage ?? "未知错误";
                PreviewError.Visibility = Visibility.Visible;
                break;
            default:
                PreviewEmpty.Visibility = Visibility.Visible;
                break;
        }

        StatusText.Text = document.Kind == PreviewKind.Error ? "预览失败" : "就绪";
    }

    private void ClearPreview()
    {
        StopTextEdit();
        _selectedFile = null;
        _currentPreview = null;
        PreviewTitle.Text = "";
        PreviewSummary.Text = "";
        TextPreview.Clear();
        JsonPreview.ItemsSource = null;
        DatPreview.ItemsSource = null;
        TextPreview.Visibility = Visibility.Collapsed;
        JsonPreview.Visibility = Visibility.Collapsed;
        DatPreview.Visibility = Visibility.Collapsed;
        PreviewError.Visibility = Visibility.Collapsed;
        PreviewEmpty.Visibility = Visibility.Visible;
        EditFileButton.Visibility = Visibility.Collapsed;
        SaveFileButton.Visibility = Visibility.Collapsed;
        CancelEditButton.Visibility = Visibility.Collapsed;
        EditorToolbar.Visibility = Visibility.Collapsed;
        ReloadPreviewButton.Visibility = Visibility.Collapsed;
    }

    private bool ConfirmDiscardEdit()
    {
        if (!_isEditingText || string.Equals(TextPreview.Text, _editingOriginalText, StringComparison.Ordinal))
            return true;

        return MessageBox.Show(
            "当前文件有未保存修改，确定放弃编辑吗？",
            "取消编辑", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private void StopTextEdit()
    {
        if (!_isEditingText && _editingPath is null)
            return;

        _isEditingText = false;
        _editingPath = null;
        _editingOriginalText = null;
        TextPreview.IsReadOnly = true;
        SaveFileButton.IsEnabled = false;
        SaveFileButton.Visibility = Visibility.Collapsed;
        CancelEditButton.Visibility = Visibility.Collapsed;
        EditorToolbar.Visibility = Visibility.Collapsed;
        PreviewPanel.SetValue(Grid.RowProperty, 2);
        PreviewPanel.SetValue(Grid.RowSpanProperty, 1);
        FileListPanel.Visibility = Visibility.Visible;
        DirTree.IsEnabled = true;
        FileList.IsEnabled = true;
        OpenFileButton.IsEnabled = _operationCts is null;
        UpdateFileLockButton();
    }

    private void IncreaseFont_Click(object sender, RoutedEventArgs e) => SetEditorFontSize(TextPreview.FontSize + 1);

    private void DecreaseFont_Click(object sender, RoutedEventArgs e) => SetEditorFontSize(TextPreview.FontSize - 1);

    private void SetEditorFontSize(double size)
    {
        TextPreview.FontSize = Math.Clamp(size, 8, 32);
        _config.EditorFontSize = TextPreview.FontSize;
        ConfigService.SavePluginConfig("DataBrowser", _config);
    }

    private static bool IsEditablePreview(PreviewDocument document)
        => document.Text is not null && document.Kind is PreviewKind.Text or PreviewKind.Json;

    // ── Extract ─────────────────────────────────────────

    private void TreeExtractFile_Click(object sender, RoutedEventArgs e)
    {
        if (DirTree.SelectedItem is TreeItemViewModel item && !item.IsDirectory && item.FileRecord is not null)
            ExtractSingleFile(item.FileRecord, item.Name);
    }

    private void TreeReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        if (DirTree.SelectedItem is TreeItemViewModel item && !item.IsDirectory && item.FileRecord is not null)
            ReplaceFile(item.FullPath, item.Name);
    }

    private void TreeExtractJson_Click(object sender, RoutedEventArgs e)
    {
        if (DirTree.SelectedItem is TreeItemViewModel item && !item.IsDirectory && item.FileRecord is not null)
            ExtractAsJson(item.FileRecord, item.FullPath, item.Name);
    }

    private void TreeCopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (DirTree.SelectedItem is TreeItemViewModel item && !item.IsDirectory)
            CopyFilePath(item.FullPath);
    }

    private void TreeExtractDir_Click(object sender, RoutedEventArgs e)
    {
        if (DirTree.SelectedItem is TreeItemViewModel item && item.IsDirectory)
            ExtractDirectory(item);
    }

    private void ExtractFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel item && item.FileRecord is not null)
            ExtractSingleFile(item.FileRecord, item.Name);
    }

    private void ReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel item && item.FileRecord is not null)
            ReplaceFile(item.FullPath, item.Name);
    }

    private void ExtractJson_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel item && item.FileRecord is not null)
            ExtractAsJson(item.FileRecord, item.FullPath, item.Name);
    }

    private void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel item)
            CopyFilePath(item.FullPath);
    }

    private void ExtractDir_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.Tag is TreeItemViewModel item && item.IsDirectory)
            ExtractDirectory(item);
    }

    private static void CopyFilePath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            Clipboard.SetText(path);
    }

    private void ExtractSingleFile(FileRecord file, string defaultName)
    {
        var dlg = new SaveFileDialog
        {
            FileName = defaultName,
            Title = "提取文件到...",
            InitialDirectory = _config.LastExtractDir,
        };
        if (dlg.ShowDialog() != true) return;

        var destination = dlg.FileName;
        _config.LastExtractDir = Path.GetDirectoryName(destination);
        ConfigService.SavePluginConfig("DataBrowser", _config);

        _ = RunExtractionAsync(async cancellationToken =>
        {
            var result = await ExtractService.ExtractFileToPathAsync(file, destination, cancellationToken);
            return result;
        });
    }

    private async void ReplaceFile(string virtualPath, string expectedName)
    {
        if (_gd is null)
        {
            MessageBox.Show("请先打开游戏数据文件。", "无法替换", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            FileName = expectedName,
            Title = "选择同名替换文件",
            InitialDirectory = _config.LastExtractDir,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        if (!string.Equals(Path.GetFileName(dialog.FileName), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                $"请选择同名文件：{expectedName}",
                "文件名不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            $"将使用以下本地文件替换游戏索引中的文件：\n\n{virtualPath}\n\n"
            + $"来源：{dialog.FileName}\n\n"
            + "操作会写入新的 Bundle 并更新索引。首次写入前会创建原始索引备份。是否继续？",
            "确认替换文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;

        var gameDataPath = _gd.GameDataPath;
        _config.LastExtractDir = Path.GetDirectoryName(dialog.FileName);
        ConfigService.SavePluginConfig("DataBrowser", _config);

        ReleaseFileLocks(keepReopenPath: true);
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true, "正在替换文件...", true);
        ProgressBar.IsIndeterminate = true;

        try
        {
            var result = await Task.Run(
                () => ReplaceService.Replace(gameDataPath, virtualPath, dialog.FileName, cts.Token),
                cts.Token);
            StatusText.Text = $"替换完成：{result.VirtualPath}";
            MessageBox.Show(
                $"已替换：{result.VirtualPath}\n"
                + $"大小：{FormatSize(result.ReplacementSize)}\n"
                + $"新 Bundle：{result.BundlePath}\n"
                + $"原始索引备份：{result.BaselinePath}",
                "替换完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消替换";
        }
        catch (Exception ex)
        {
            StatusText.Text = "替换失败";
            MessageBox.Show($"替换失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            var status = StatusText.Text;
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            cts.Dispose();
            SetBusy(false, status, false);
        }

        OpenPath(gameDataPath);
    }

    private void ExtractAsJson(FileRecord file, string sourcePath, string defaultName)
    {
        if (!IsDatTablePath(sourcePath))
        {
            MessageBox.Show("仅支持提取 .dat、.dat64、.datc64 或 .datcl64 文件为 JSON。",
                "不支持的文件类型", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(defaultName) + ".json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON 文件|*.json|所有文件|*.*",
            Title = "提取为 JSON...",
            InitialDirectory = _config.LastExtractDir,
        };
        if (dlg.ShowDialog() != true) return;

        var destination = dlg.FileName;
        _config.LastExtractDir = Path.GetDirectoryName(destination);
        ConfigService.SavePluginConfig("DataBrowser", _config);

        _ = RunExtractionAsync(cancellationToken =>
            ExtractService.ExtractDatc64AsJsonAsync(file, sourcePath, destination, cancellationToken));
    }

    private static bool IsDatTablePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dat64", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".datc64", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".datcl64", StringComparison.OrdinalIgnoreCase);
    }

    private void ExtractDirectory(TreeItemViewModel dirItem)
    {
        var dirNode = _rootNode is not null ? FindDirNode(_rootNode, dirItem.FullPath) : null;
        if (dirNode is null && (_cachedTree is null || _gd is null)) return;

        var dlg = new SaveFileDialog
        {
            FileName = "__select_folder__",
            Title = "选择提取目标文件夹（文件名随意，取其目录）",
            InitialDirectory = _config.LastExtractDir,
        };
        if (dlg.ShowDialog() != true) return;
        var destination = Path.GetDirectoryName(dlg.FileName);
        if (string.IsNullOrEmpty(destination)) return;

        _config.LastExtractDir = destination;
        ConfigService.SavePluginConfig("DataBrowser", _config);

        _ = RunExtractionAsync(cancellationToken =>
        {
            var progress = new Progress<ExtractionProgress>(UpdateExtractionProgress);
            if (dirNode is not null)
                return ExtractService.ExtractDirectoryAsync(dirNode, destination, progress, cancellationToken);

            var files = _cachedTree!.GetFilesUnder(dirItem.FullPath, _gd!.Index.Files);
            return ExtractService.ExtractFilesAsync(
                files, dirItem.FullPath, destination, progress, cancellationToken);
        });
    }

    private async Task RunExtractionAsync(Func<CancellationToken, Task<ExtractionResult>> action)
    {
        _operationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true, "提取中...", true);
        ProgressBar.IsIndeterminate = true;

        try
        {
            var result = await action(cts.Token);
            if (result.IsCancelled)
                StatusText.Text = $"已取消：完成 {result.CompletedFiles:N0} 个文件";
            else
                StatusText.Text = $"提取完成：成功 {result.CompletedFiles:N0}，失败 {result.FailedFiles:N0}";

            if (result.FailedFiles > 0)
            {
                var paths = string.Join("\n", result.FailedPaths.Take(10));
                MessageBox.Show($"有 {result.FailedFiles:N0} 个文件提取失败。\n\n{paths}",
                    "提取结果", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "提取失败";
            MessageBox.Show($"提取失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
                SetBusy(false, StatusText.Text, false);
            }
            cts.Dispose();
        }
    }

    private void UpdateExtractionProgress(ExtractionProgress progress)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Maximum = Math.Max(1, progress.TotalFiles);
        var processed = progress.CompletedFiles + progress.FailedFiles;
        ProgressBar.Value = processed;
        StatusText.Text = $"提取中 {processed:N0}/{progress.TotalFiles:N0}";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        StatusText.Text = "正在取消...";
    }

    // ── Helpers ─────────────────────────────────────────

    private void SetBusy(bool busy, string status, bool canCancel)
    {
        StatusText.Text = status;
        OpenFileButton.IsEnabled = !busy;
        FileLockButton.IsEnabled = !busy && (_gd is not null || !string.IsNullOrWhiteSpace(_reopenPath));
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy && canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFileLockButton()
    {
        if (_gd is not null)
        {
            FileLockButton.Content = "释放文件占用";
            FileLockButton.ToolTip = "关闭游戏数据索引与 Bundle 文件句柄，以便启动游戏。";
        }
        else
        {
            var canReopen = !string.IsNullOrWhiteSpace(_reopenPath);
            FileLockButton.Content = canReopen ? "重新打开" : "释放文件占用";
            FileLockButton.ToolTip = canReopen
                ? "重新打开上次释放的数据源。"
                : "打开游戏数据后，可释放相关文件占用。";
        }

        FileLockButton.IsEnabled = _operationCts is null
            && (_gd is not null || !string.IsNullOrWhiteSpace(_reopenPath));
    }

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
