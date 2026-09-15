using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

/// <summary>
/// 「创建补丁」模块：特效补丁管理介绍 + 补丁生成器（diff 两份索引自动生成补丁）。
/// </summary>
public partial class FxPatchCreatorView : UserControl
{
    private readonly IEventBus _eventBus;
    private bool _settingVanillaDefault;
    private bool _vanillaAutoSelected;
    private string? _gameDataPath;
    private PoeGameKind _gameKind;

    public FxPatchCreatorView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        InitializeComponent();
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
    }

    public void Dispose() => _eventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);

    private void OnGameContextChanged(GameContextChanged context)
    {
        if (string.IsNullOrWhiteSpace(context.GameDataPath))
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameContextChanged(context));
            return;
        }

        _gameDataPath = context.GameDataPath;
        _gameKind = context.Game;
        _ = SetDefaultVanillaIndexAsync();
    }

    private void DiffVanillaBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingVanillaDefault)
            _vanillaAutoSelected = false;
    }

    private async Task SetDefaultVanillaIndexAsync()
    {
        if (_gameKind != PoeGameKind.Poe2
            || DiffVanillaBox is null
            || (!_vanillaAutoSelected && DiffVanillaBox.Text.Trim().Length > 0))
            return;

        var gameData = _gameDataPath ?? GameDataPathPreference.Get();
        if (string.IsNullOrWhiteSpace(gameData))
            return;

        string resolvedGameData;
        try
        {
            resolvedGameData = GameDataAccess.ResolvePath(gameData);
        }
        catch
        {
            // The user may still be typing or may need to browse for the game data.
            return;
        }

        string? baseline;
        try
        {
            baseline = await Task.Run(() => IndexBackupService.EnsureBaselineExists(resolvedGameData));
        }
        catch (Exception ex)
        {
            FileLogger.App.Error("Failed to create original index backup for patch generation.", ex);
            SetStatus("无法创建原始索引备份，请检查游戏目录写入权限。", UiStatus.Kind.Error);
            return;
        }

        if (string.IsNullOrEmpty(baseline))
        {
            SetStatus("无法确定原版索引位置，请手动选择原版索引。", UiStatus.Kind.Warning);
            return;
        }

        if (!string.Equals((_gameDataPath ?? GameDataPathPreference.Get())?.Trim(), gameData, StringComparison.Ordinal)
            || (!_vanillaAutoSelected && DiffVanillaBox.Text.Trim().Length > 0))
            return;

        _settingVanillaDefault = true;
        try
        {
            DiffVanillaBox.Text = baseline;
            _vanillaAutoSelected = true;
        }
        finally
        {
            _settingVanillaDefault = false;
        }
    }

    private void BrowseVanilla_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择原版索引", Filter = "索引 (*.index.bin;*.bin)|*.index.bin;*.bin" };
        if (dlg.ShowDialog() == true)
            DiffVanillaBox.Text = dlg.FileName;
    }

    private void BrowseModified_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择修改后索引", Filter = "索引 (_.index.bin;*.bin)|_.index.bin;*.bin" };
        if (dlg.ShowDialog() == true)
            DiffModifiedBox.Text = dlg.FileName;
    }

    private async void Diff_Click(object sender, RoutedEventArgs e)
    {
        if (_gameKind != PoeGameKind.Poe2)
        {
            SetStatus("补丁生成器仅支持 POE2 客户端。", UiStatus.Kind.Warning);
            return;
        }

        var vanilla = DiffVanillaBox.Text.Trim();
        var modified = DiffModifiedBox.Text.Trim();
        if (vanilla.Length == 0 || modified.Length == 0 || !File.Exists(vanilla) || !File.Exists(modified))
        {
            SetStatus("请选择有效的原版索引与修改后索引。", UiStatus.Kind.Warning);
            return;
        }
        // 名字留空 → 默认时间戳名（diff-年月日-时分）；有名字就用名字（非法字符已由引擎清理）
        var patchId = FxDiff.MakePatchId(DiffNameBox.Text);
        // 与引擎默认一致：生成物落 %LOCALAPPDATA%\PoEToolbox\patches\<patchId>\
        var outDir = Path.Combine(ConfigService.PatchesDirectory, patchId);
        // 不传 --bundle：引擎会从 patchId 派生 bundle 名，避免所有 diff 补丁共用一个前缀、
        // 在同样的版本号下互相覆盖。
        var args = new List<string>
        {
            "diff", vanilla, modified,
            "-o", outDir,
            "--id", patchId,
        };
        if (DiffZipCheck.IsChecked == true)
            args.Add("--zip");
        var target = DiffZipCheck.IsChecked == true ? outDir + ".zip" : outDir;
        var ok = await RunEngineAsync($"生成补丁（输出到 {target}）", args.ToArray());
        // 生成成功后打开产物所在目录并选中产物，省得自己去 %LOCALAPPDATA% 里翻
        if (ok)
            RevealInExplorer(target);
    }

    /// <summary>在资源管理器里定位并选中产物（目录则选中该目录本身）。失败只记日志，不打断用户。</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var reveal = File.Exists(full) || Directory.Exists(full)
                ? full
                : Path.GetDirectoryName(full) ?? full;
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { "/select,", reveal },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn($"Failed to reveal patch output: {path} — {ex.Message}");
        }
    }

    /// <summary>生成补丁：跳过游戏数据校验（diff 的两份索引由用户在界面选择），产物目录打开在资源管理器里。</summary>
    private Task<bool> RunEngineAsync(string action, string[] args)
        => FxEngineRunner.RunAsync(
            action,
            [new FxEngineRunner.Invocation(BuiltInId: null, args)],
            gameDataPath: _gameDataPath,
            skipGameData: true,
            clearLog: Output.ClearLog,
            appendLog: AppendLog,
            setStatus: SetStatus,
            setBusy: enabled => DiffButton.IsEnabled = enabled);

    private void AppendLog(string line) => Output.AppendLog(line);

    private void SetStatus(string text, UiStatus.Kind kind = UiStatus.Kind.Neutral)
        => Output.SetStatus(text, kind);
}
