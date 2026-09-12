using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PoEToolbox.Plugins.DataBrowser.Services;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser;

public partial class MapNumberView : UserControl
{
    private const string Poe1PreviewPath = "art/2ditems/maps/atlas2maps/new/mapnumbers15.dds";
    private const string Poe2PreviewPath = "art/2ditems/maps/endgamemaps/endgamemap15.dds";

    private GameDataAccess? _gameData;
    private CancellationTokenSource? _operationCts;
    private MapNumberSettingsView? _settings;

    public MapNumberView()
    {
        InitializeComponent();
    }

    public void ReleaseFileLocks()
    {
        _operationCts?.Cancel();
        _operationCts = null;
        _gameData?.Dispose();
        _gameData = null;
        ShowFilePicker();
        SetBusy(false, "已释放游戏数据文件占用", false);
        MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "游戏数据文件|*.ggpk;*.index.bin|Content.ggpk|*.ggpk|Index|*.index.bin",
            Title = "选择 Content.ggpk 或 _.index.bin",
        };

        if (dialog.ShowDialog() == true)
        {
            GgpkPathBox.Text = dialog.FileName;
            GgpkStatus.Text = dialog.FileName;
            GgpkStatus.Visibility = Visibility.Visible;
            OpenPath(dialog.FileName);
        }
    }

    private async void OpenPath(string path)
    {
        ReleaseFileLocks();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        GameDataAccess? opened = null;

        try
        {
            SetBusy(true, "正在打开 GGPK...", true);
            opened = await Task.Run(() => GameDataAccess.Open(GameDataLoader.ResolvePath(path), readOnly: false), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            var mapCount = opened.IsPoe2Client ? 15 : 16;
            var previewPath = opened.IsPoe2Client ? Poe2PreviewPath : Poe1PreviewPath;
            if (!opened.TryGetFile(previewPath, out var previewRecord) || previewRecord is null)
                throw new FileNotFoundException("未找到地图数字纹理，无法生成预览。", previewPath);

            var previewDds = await Task.Run(() => previewRecord.Read().ToArray(), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            _gameData = opened;
            opened = null;

            ShowSettings(previewDds, mapCount);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消打开", UiStatus.Kind.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("❌ 打开失败", UiStatus.Kind.Error);
            FileLogger.App.Error("MapNumberView failed to open game data.", ex);
            MessageBox.Show($"无法打开地图标签设置：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
        }
    }

    private void ShowSettings(byte[] previewDds, int mapCount)
    {
        _settings = new MapNumberSettingsView(
            previewDds,
            mapCount,
            defaultOffsetX: mapCount == 15 ? 0f : -1f,
            defaultOffsetY: mapCount == 15 ? 0f : 3f);
        SettingsHost.Content = _settings;
        SettingsPlaceholder.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = true;
        SetStatus("已打开地图标签设置");
    }

    private void ShowFilePicker()
    {
        _settings = null;
        SettingsHost.Content = null;
        SettingsPlaceholder.Visibility = Visibility.Visible;
        ApplyButton.IsEnabled = false;
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
            await ApplySettingsAsync(_settings);
    }

    private async Task ApplySettingsAsync(MapNumberSettingsView settings)
    {
        if (_gameData is null)
            return;

        _operationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true, "正在修改地图标签...", true);
        ProgressBar.IsIndeterminate = true;

        var fontSize = settings.SelectedFontSize;
        var offsetX = settings.SelectedOffsetX;
        var offsetY = settings.SelectedOffsetY;
        var fontFamily = settings.SelectedFontFamily;
        var colors = settings.SelectedColors;
        var poe2BackgroundImage = _gameData.IsPoe2Client ? LoadMapBackgroundBytes() : null;

        try
        {
            var progress = new Progress<string>(SetStatus);
            var result = await Task.Run(
                () => MapNumberReplacementService.Apply(
                    _gameData, progress, cts.Token, fontSize, offsetX, offsetY, fontFamily,
                    colors, poe2BackgroundImage),
                cts.Token);

            ReleaseFileLocks();
            SetStatus($"✅ 写入完成：{result.UpdatedFiles} 个文件", UiStatus.Kind.Success);
            FileLogger.App.Info($"MapNumber settings applied: {result.UpdatedFiles} file(s).");
            MessageBox.Show(
                $"已写入 {result.UpdatedFiles} 个 DDS。\n"
                + $"字体：{fontFamily}\n"
                + $"字号：{fontSize:0} px，偏移：X {offsetX:0} / Y {offsetY:0} px\n"
                + $"新 bundle：{result.BundlePath}\n"
                + $"原始索引备份：{result.BaselinePath}",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消写入", UiStatus.Kind.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("❌ 写入失败", UiStatus.Kind.Error);
            FileLogger.App.Error("MapNumberView apply failed.", ex);
            MessageBox.Show($"写入失败：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
            {
                var status = StatusText.Text;
                var kind = _lastStatusKind;
                ReleaseFileLocks();
                SetStatus(status, kind);
            }
            cts.Dispose();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        SetStatus("正在取消...");
    }

    private void SetBusy(bool busy, string status, bool canCancel)
    {
        SetStatus(status);
        OpenFileButton.IsEnabled = !busy;
        SettingsHost.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _settings is not null;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy && canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    private UiStatus.Kind _lastStatusKind = UiStatus.Kind.Neutral;

    private void SetStatus(string status, UiStatus.Kind kind)
    {
        _lastStatusKind = kind;
        UiStatus.Set(StatusText, status, kind);
        OutputText.Text = status;
    }

    private void SetStatus(string status)
        => SetStatus(status, UiStatus.Kind.Neutral);

    private static byte[] LoadMapBackgroundBytes()
    {
        var uri = new Uri(
            "pack://application:,,,/PoEToolbox.Plugins.DataBrowser;component/Assets/mapBackground.png",
            UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("未找到 PoE1 地图底图素材。");
        using var stream = resource.Stream;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
