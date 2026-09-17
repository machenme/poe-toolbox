using System.IO;
using System.Windows;
using System.Windows.Controls;
using PoEToolbox.Plugins.DataBrowser.Services;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser;

public partial class MapNumberView : UserControl
{
    // 预览只需要任何一个地图数字文件当画布模板，取各自客户端的 15 号（两边都存在）。
    private static readonly string Poe1PreviewPath = MapNumberDefaults.Poe1PathPrefix + "15.dds";
    private static readonly string Poe2PreviewPath = MapNumberDefaults.Poe2PathPrefix + "15.dds";

    /// <summary>
    /// Canvas template for the preview, shipped inside the exe. Extracted from a client with
    /// <c>extract-file &lt;game-data&gt; art/2ditems/maps/endgamemaps/endgamemap15.dds</c>.
    /// </summary>
    /// <remarks>
    /// Only the geometry is used: the renderer clears the canvas and draws the number onto it, so the
    /// pixels of this file never show up, and the same template is representative for both clients —
    /// which is what keeps entering this module free of game data access. The plate that is actually
    /// seen behind the number is <c>Assets/mapBackground.png</c>, drawn by the settings view.
    /// </remarks>
    private const string EmbeddedPreview = "Assets/mapnumber-preview.dds";

    private readonly IEventBus _eventBus;
    private string? _gameDataPath;
    private CancellationTokenSource? _operationCts;
    private MapNumberSettingsView? _settings;
    private bool _isPoe2;
    private bool _busy;

    public MapNumberView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        InitializeComponent();
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
    }

    public void Dispose() => _eventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);

    private void OnGameContextChanged(GameContextChanged context)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameContextChanged(context));
            return;
        }

        // Entering this module must not read the client: a full index is around 1 GB and takes seconds
        // to parse. Everything here only records which client to work on, and the preview comes from
        // the texture embedded in this assembly where one is shipped (PoE2); otherwise the user asks
        // for it with "读取游戏数据". Either way the client is only opened for the write. The shell
        // re-publishes the context every time the module is activated, so this runs often and has to
        // stay free of data access.
        var path = context.GameDataPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _gameDataPath = null;
            ResetToPrompt("请先在主窗口选择游戏数据");
            return;
        }

        if (string.Equals(_gameDataPath, path, StringComparison.OrdinalIgnoreCase))
        {
            // Same client as before: keep the settings that are already loaded.
            if (context.Game == PoeGameKind.Unknown)
                SetStatus("正在等待主窗口识别客户端版本...", UiStatus.Kind.Warning);
            return;
        }

        _gameDataPath = path;
        if (context.Game == PoeGameKind.Unknown)
        {
            ResetToPrompt("正在等待主窗口识别客户端版本...");
            SetStatus("正在等待主窗口识别客户端版本...", UiStatus.Kind.Warning);
            return;
        }
        // One embedded template serves both clients (it only supplies the canvas geometry), so the
        // preview costs no game data at all. Without the resource a client still gets the manual
        // "读取游戏数据" step.
        var embeddedPreview = TryLoadEmbeddedPreview();
        if (embeddedPreview is not null)
        {
            _isPoe2 = context.Game == PoeGameKind.Poe2;
            ShowSettings(embeddedPreview, _isPoe2 ? MapNumberDefaults.Poe2MapCount : MapNumberDefaults.Poe1MapCount);
            PreviewHint.Text = "预览来自工具箱内置素材（未读取游戏数据），点“写入地图标签”时才打开客户端。";
            SetStatus("已显示内置预览");
            return;
        }

        ResetToPrompt("点击“读取游戏数据”载入地图标签设置");
        PreviewHint.Text = "点击左侧“读取游戏数据”载入预览，读取完立即释放；写入时才打开客户端。";
    }

    /// <summary>Reads the preview canvas template embedded in the assembly, or null when there is none.</summary>
    private static byte[]? TryLoadEmbeddedPreview()
    {
        var uri = new Uri(
            $"pack://application:,,,/PoEToolbox.Plugins.DataBrowser;component/{EmbeddedPreview}",
            UriKind.Absolute);
        try
        {
            var resource = Application.GetResourceStream(uri);
            if (resource is null)
                return null;

            using var stream = resource.Stream;
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
        catch (IOException)
        {
            // No embedded template (a build without the resource): fall back to reading the client.
            return null;
        }
    }

    /// <summary>Shows the "nothing loaded yet" state. No client data is held at this point.</summary>
    private void ResetToPrompt(string message)
    {
        _settings = null;
        SettingsHost.Content = null;
        SettingsPlaceholder.Text = message;
        SettingsPlaceholder.Visibility = Visibility.Visible;
        LoadButton.Visibility = Visibility.Visible;
        LoadButton.IsEnabled = _gameDataPath is not null;
        ApplyButton.IsEnabled = false;
        UpdateRestoreAvailability();
        OutputText.Text = message;
    }

    /// <summary>
    /// 「恢复到上一次改动前」只在真的存在改动前快照时可点。快照是第一次写入地图标签时留下的，
    /// 恢复成功后即失效，所以这里每次都重新读一遍小 json，不去猜状态。
    /// </summary>
    private void UpdateRestoreAvailability()
    {
        var path = _gameDataPath;
        var hasSnapshot = !string.IsNullOrWhiteSpace(path) && MapNumberSnapshot.Exists(path);
        RestoreButton.IsEnabled = hasSnapshot && !_busy;

        if (hasSnapshot)
        {
            // 说明恢复到什么时候：快照时间点比「原版」这种说法更准确，用户改过好几轮时尤其如此。
            var createdAt = MapNumberSnapshot.CreatedAt(path!);
            RestoreHint.Text = createdAt is { } time
                ? $"恢复到 {time:yyyy-MM-dd HH:mm} 写入前保存的地图文件；恢复后这份快照失效。"
                : "恢复到你第一次写入地图标签之前的版本；恢复后这份快照失效。";
        }
        else
        {
            RestoreHint.Text = "还没有可撤回的改动：第一次点「写入地图标签」时会自动保存，之后这里才能用。";
        }
    }

    /// <summary>
    /// Called when the module is left, the app shuts down, or the shell asks for the game data locks
    /// back. No index is held between actions any more, so there is nothing to release — only a
    /// running operation to cancel and its memory to hand back.
    /// </summary>
    public void ReleaseFileLocks()
    {
        _operationCts?.Cancel();
        _operationCts = null;
        SetBusy(false, "已释放游戏数据文件占用", false);
        MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _gameDataPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("请先在主窗口选择游戏数据", UiStatus.Kind.Warning);
            return;
        }

        _operationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _operationCts = cts;

        try
        {
            SetBusy(true, "正在读取地图数字纹理…", true);

            // The preview needs the client for one small texture, so the index is opened, read and
            // closed again in one scope: nothing stays resident while the settings are adjusted.
            var preview = await GameDataLoader.UseAsync(
                path,
                GameDataMode.Read,
                (gd, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var isPoe2 = gd.IsPoe2Client;
                    var previewPath = isPoe2 ? Poe2PreviewPath : Poe1PreviewPath;
                    if (!gd.TryGetFile(previewPath, out var previewRecord) || previewRecord is null)
                        throw new FileNotFoundException("未找到地图数字纹理，无法生成预览。", previewPath);

                    return Task.FromResult((Dds: previewRecord.Read().ToArray(), IsPoe2: isPoe2));
                },
                cts.Token);

            if (!ReferenceEquals(_operationCts, cts))
                return;

            _isPoe2 = preview.IsPoe2;
            ShowSettings(preview.Dds, _isPoe2 ? MapNumberDefaults.Poe2MapCount : MapNumberDefaults.Poe1MapCount);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消读取", UiStatus.Kind.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("❌ 读取失败", UiStatus.Kind.Error);
            FileLogger.App.Error("MapNumberView failed to read the map number preview.", ex);
            MessageBox.Show($"无法读取地图标签设置：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void ShowSettings(byte[] previewDds, int mapCount)
    {
        _settings = new MapNumberSettingsView(
            previewDds,
            mapCount,
            defaultOffsetX: mapCount == MapNumberDefaults.Poe2MapCount ? 0f : -1f,
            defaultOffsetY: mapCount == MapNumberDefaults.Poe2MapCount ? 0f : 3f);
        SettingsHost.Content = _settings;
        SettingsPlaceholder.Visibility = Visibility.Collapsed;
        LoadButton.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = true;
        SetStatus("已打开地图标签设置");
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
            await ApplySettingsAsync(_settings);
    }

    private async Task ApplySettingsAsync(MapNumberSettingsView settings)
    {
        var path = _gameDataPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fontSize = settings.SelectedFontSize;
        var offsetX = settings.SelectedOffsetX;
        var offsetY = settings.SelectedOffsetY;
        var fontFamily = settings.SelectedFontFamily;
        var colors = settings.SelectedColors;
        var poe2BackgroundImage = _isPoe2 ? LoadMapBackgroundBytes() : null;

        // 第一次写入前的体检：地图文件若已被其他补丁改过，这里先说清楚，别默默盖掉。
        // 只读打开一次做内容比对，失败不拦路（拿不到基线就当没信息）。
        MapNumberForeignContent? inspection = null;
        try
        {
            inspection = await GameDataLoader.UseAsync(
                path,
                GameDataMode.Read,
                (gameData, _) => Task.FromResult(MapNumberReplacementService.InspectBeforeWrite(gameData)),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn("地图标签写入前检查未能完成，直接继续。", ex);
        }

        if (inspection is { IsFirstWrite: true, ForeignFiles.Count: > 0 })
        {
            var list = string.Join("、", inspection.ForeignFiles.Take(6));
            if (inspection.ForeignFiles.Count > 6)
                list += $" 等 {inspection.ForeignFiles.Count} 个文件";
            var confirm = MessageBox.Show(
                "检测到这些地图文件的内容不是游戏原版，可能被其他补丁改过：\n\n"
                + list + "\n\n"
                + "继续写入会把上面这些内容**覆盖掉**，而且这次覆盖之后不好单独找回（「恢复到上一次改动前」"
                + "会把你带回到现在的状态，但不会带回对方的改动）。\n\n"
                + "建议先退出，用「恢复游戏原版」或启动器的验证功能确认客户端状态后再回来。\n\n"
                + "确定继续写入？",
                "地图文件已被修改", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                SetStatus("已取消写入（检测到地图文件被其他补丁修改）", UiStatus.Kind.Warning);
                return;
            }
        }

        _operationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true, "正在修改地图标签...", true);
        ProgressBar.IsIndeterminate = true;

        try
        {
            var progress = new Progress<string>(SetStatus);
            // Opened only for the write — this is also where "the game must be closed" is enforced —
            // and closed again with the rest of the scope, so the index lives for the write only.
            var result = await GameDataLoader.UseAsync(
                path,
                GameDataMode.ReadWrite,
                (gameData, token) => Task.FromResult(MapNumberReplacementService.Apply(
                    gameData, progress, token, fontSize, offsetX, offsetY, fontFamily,
                    colors, poe2BackgroundImage)),
                cts.Token);

            var summary = $"✅ 写入完成：{result.UpdatedFiles} 个文件";
            SetStatus(summary, UiStatus.Kind.Success);
            FileLogger.App.Info($"MapNumber settings applied: {result.UpdatedFiles} file(s).");
            MessageBox.Show(
                $"已写入 {result.UpdatedFiles} 个 DDS。\n"
                + $"字体：{fontFamily}\n"
                + $"字号：{fontSize:0} px，偏移：X {offsetX:0} / Y {offsetY:0} px\n"
                + $"原始索引备份：{result.BaselinePath}\n"
                + (result.SnapshotCreated
                    ? "已保存改动前的存档，之后可以点「恢复到上一次改动前」撤回。"
                    : "改动前的存档是之前那次写入留下的，「恢复」仍会回到那一次之前。"),
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
            ProgressBar.IsIndeterminate = false;
            if (ReferenceEquals(_operationCts, cts))
            {
                // The settings stay loaded: only the write handle is gone, and re-applying is free.
                _operationCts = null;
                var status = StatusText.Text;
                var kind = _lastStatusKind;
                SetBusy(false, status, false);
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

    /// <summary>
    /// Puts the map numbers back to what they were before the first "写入地图标签" of this install.
    /// The content comes from the snapshot taken at that moment, so anything the client already had
    /// back then — including another patch's version of these files — comes back too. Only this group
    /// of files is written; other modules' changes are untouched.
    /// </summary>
    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _gameDataPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("请先在主窗口选择游戏数据", UiStatus.Kind.Warning);
            return;
        }

        if (!MapNumberSnapshot.Exists(path))
        {
            UpdateRestoreAvailability();
            SetStatus("没有可撤回的改动。", UiStatus.Kind.Warning);
            return;
        }

        var createdAt = MapNumberSnapshot.CreatedAt(path);
        var confirm = MessageBox.Show(
            "将把地图数字（1~" + (_isPoe2 ? MapNumberDefaults.Poe2MapCount : MapNumberDefaults.Poe1MapCount) + " 号）及各自的低 mip "
            + "恢复到"
            + (createdAt is { } time ? $"{time:yyyy-MM-dd HH:mm} " : "")
            + "第一次写入地图标签之前的内容。\n\n"
            + "只会改动这一组文件，其他模块的设置不受影响。恢复后这份存档会用掉，想再改就是一次全新的改动。\n\n"
            + "确定继续？",
            "恢复到上一次改动前", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        _operationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true, "正在恢复到上一次改动前...", true);
        ProgressBar.IsIndeterminate = true;

        try
        {
            var progress = new Progress<string>(SetStatus);
            var result = await GameDataLoader.UseAsync(
                path,
                GameDataMode.ReadWrite,
                (gameData, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(MapNumberRestoreService.Restore(
                        gameData,
                        message =>
                        {
                            FileLogger.App.Info(message);
                            ((IProgress<string>)progress).Report(message);
                        }));
                },
                cts.Token);

            if (!ReferenceEquals(_operationCts, cts))
                return;

            var summary = result.RestoredFiles > 0
                ? $"已恢复 {result.RestoredFiles} 个文件（地图数字及其低 mip）。"
                : "地图数字已经是改动前的内容。";
            SetStatus($"✅ {summary}", UiStatus.Kind.Success);
            FileLogger.App.Info($"MapNumber restore: {result.RestoredFiles} file(s) restored.");

            // The settings view still shows the previous choices; drop it so the panel comes back
            // with the defaults instead of implying those values are still written into the client.
            ResetToPrompt("已恢复到上一次改动前。再次写入前请重新读取游戏数据。");
            PreviewHint.Text = "点击左侧“读取游戏数据”载入预览，读取完立即释放；写入时才打开客户端。";

            MessageBox.Show(summary + "\n\n想继续调整，请先点「读取游戏数据」重新载入。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消恢复", UiStatus.Kind.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("❌ 恢复失败", UiStatus.Kind.Error);
            FileLogger.App.Error("MapNumberView restore failed.", ex);
            MessageBox.Show($"恢复到上一次改动前失败：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgressBar.IsIndeterminate = false;
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
                var status = StatusText.Text;
                var kind = _lastStatusKind;
                SetBusy(false, status, false);
                SetStatus(status, kind);
            }
            cts.Dispose();
        }
    }

    private void SetBusy(bool busy, string status, bool canCancel)
    {
        _busy = busy;
        SetStatus(status);
        SettingsHost.IsEnabled = !busy;
        LoadButton.IsEnabled = !busy && _gameDataPath is not null && _settings is null;
        ApplyButton.IsEnabled = !busy && _settings is not null;
        UpdateRestoreAvailability();
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
