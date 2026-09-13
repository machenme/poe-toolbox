using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

public partial class FxPatchView : UserControl
{
    private readonly IEventBus _eventBus;
    private bool _running;
    private bool _settingVanillaDefault;
    private bool _vanillaAutoSelected;
    private bool _settingPatchPath;
    private string? _gameDataPath;
    private PoeGameKind _gameKind;

    public FxPatchView(IEventBus? eventBus = null)
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

        string baseline;
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

    private void BrowsePatch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择补丁描述文件", Filter = "补丁描述 (*.patch.json;*.json;*.zip)|*.patch.json;*.json;*.zip|全部文件 (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
            SetCustomPatchPath(dlg.FileName);
    }

    /// <summary>填入路径并自动切到「自定义补丁」来源——用户不必先去改下拉菜单。</summary>
    private void SetCustomPatchPath(string path)
    {
        _settingPatchPath = true;
        try
        {
            PatchJsonBox.Text = path;
            if (PatchSourceBox.SelectedIndex != 1)
                PatchSourceBox.SelectedIndex = 1;
        }
        finally
        {
            _settingPatchPath = false;
        }
    }

    private void PatchJsonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 手输/粘贴路径也当作选了自定义补丁，避免「填了路径却仍在用内置补丁」
        if (_settingPatchPath || PatchSourceBox is null)
            return;
        if (PatchJsonBox.Text.Trim().Length > 0 && PatchSourceBox.SelectedIndex != 1)
            PatchSourceBox.SelectedIndex = 1;
        UpdateCustomPathHint();
    }

    private void PatchSource_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCustomPathHint();

    /// <summary>下拉菜单停在内置、却填着自定义路径时给一句提示，避免「填了路径却仍在用内置补丁」。</summary>
    private void UpdateCustomPathHint()
    {
        if (PatchJsonBox is null || PatchSourceBox is null || CustomPathHint is null)
            return;
        var stale = PatchSourceBox.SelectedIndex != 1 && PatchJsonBox.Text.Trim().Length > 0;
        CustomPathHint.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
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

    // ═══ 引擎调用 ═══════════════════════════════════════════════
    private bool ValidateInputs(out string gameData, out string? patchJson)
    {
        gameData = (_gameDataPath ?? GameDataPathPreference.Get())?.Trim() ?? string.Empty;
        patchJson = null;
        if (_gameKind != PoeGameKind.Poe2)
        {
            SetStatus("特效补丁仅支持 POE2 客户端。", UiStatus.Kind.Warning);
            return false;
        }
        if (gameData.Length == 0)
        {
            SetStatus("请先选择游戏数据。", UiStatus.Kind.Warning);
            return false;
        }
        if (PatchSourceBox.SelectedIndex == 1)
        {
            patchJson = PatchJsonBox.Text.Trim();
            if (patchJson.Length == 0 || !File.Exists(patchJson))
            {
                SetStatus("请选择有效的补丁描述文件（patch.json 或 .zip 补丁包）。", UiStatus.Kind.Warning);
                return false;
            }
        }
        return true;
    }

    private async void Status_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var gameData, out var patchJson))
            return;
        var builtIn = patchJson is null;
        var args = builtIn
            ? new[] { gameData, "status" }
            : new[] { gameData, patchJson!, "status" };
        await RunEngineAsync("刷新补丁状态", builtIn, args);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var gameData, out var patchJson))
            return;
        var builtIn = patchJson is null;
        var args = builtIn
            ? new[] { gameData, "apply" }
            : new[] { gameData, patchJson!, "apply" };
        await RunEngineAsync("启用特效补丁", builtIn, args);
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var gameData, out var patchJson))
            return;
        var builtIn = patchJson is null;
        var args = builtIn
            ? new[] { gameData, "revert" }
            : new[] { gameData, patchJson!, "revert" };
        await RunEngineAsync("恢复游戏原版", builtIn, args);
    }

    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var gameData, out var patchJson))
            return;
        var confirm = MessageBox.Show(
            "将删除本补丁新增的文件。被补丁改动过的游戏原有文件会保留，不会弄坏客户端。\n\n删掉之后想再用，需要重新应用补丁。确定继续？\n（只是想临时关掉特效的话，请用「恢复游戏原版」。）",
            "完全卸载补丁", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;
        var builtIn = patchJson is null;
        var args = builtIn
            ? new[] { gameData, "purge" }
            : new[] { gameData, patchJson!, "purge" };
        await RunEngineAsync("完全卸载补丁", builtIn, args);
    }

    private async void Diff_Click(object sender, RoutedEventArgs e)
    {
        if (_gameKind != PoeGameKind.Poe2)
        {
            SetStatus("特效补丁仅支持 POE2 客户端。", UiStatus.Kind.Warning);
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
        var ok = await RunEngineAsync($"生成补丁（输出到 {target}）", builtIn: false, args.ToArray(), skipGameData: true);
        // 生成成功后打开产物所在目录并选中产物，省得自己去 %LOCALAPPDATA% 里翻
        if (ok)
            RevealInExplorer(target);
    }

    private void AdvancedToggle_Changed(object sender, RoutedEventArgs e)
    {
        var open = AdvancedToggle.IsChecked == true;
        AdvancedPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedToggle.Content = open ? "▾ 高级维护" : "▸ 高级维护";
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

    private async Task<bool> RunEngineAsync(string action, bool builtIn, string[] args, bool skipGameData = false)
    {
        if (_running)
        {
            SetStatus("已有任务在执行，请稍候。", UiStatus.Kind.Warning);
            return false;
        }
        var sharedPath = _gameDataPath ?? GameDataPathPreference.Get();
        if (!skipGameData && (string.IsNullOrWhiteSpace(sharedPath)
            || (!File.Exists(sharedPath) && !Directory.Exists(sharedPath))))
        {
            SetStatus("游戏数据路径无效。", UiStatus.Kind.Warning);
            return false;
        }

        _running = true;
        SetButtonsEnabled(false);
        Output.ClearLog();
        SetStatus($"{action}……执行中（打开索引需要数秒）");

        var exitCode = await Task.Run(() =>
        {
            FxPatchEngine.LogSink = s => AppendLog(s);
            FxDiff.LogSink = s => AppendLog(s);
            try
            {
                return FxPatchEngine.Run(args, builtIn);
            }
            finally
            {
                FxPatchEngine.LogSink = null;
                FxDiff.LogSink = null;
            }
        });

        _running = false;
        SetButtonsEnabled(true);

        if (exitCode == 0)
        {
            SetStatus($"✅ {action}成功。", UiStatus.Kind.Success);
            FileLogger.App.Info($"UI {action}: success.");
            AppendLog("");
            AppendLog($"════════════ ✅ {action}成功 ════════════");
            return true;
        }
        else
        {
            SetStatus($"❌ {action}失败（退出码 {exitCode}），详见下方引擎输出。", UiStatus.Kind.Error);
            FileLogger.App.Error($"UI {action}: failed with exit code {exitCode}.");
            AppendLog("");
            AppendLog($"════════════ ❌ {action}失败（退出码 {exitCode}）════════════");
            return false;
        }
    }

    private void AppendLog(string line) => Output.AppendLog(line);

    private void SetStatus(string text, UiStatus.Kind kind = UiStatus.Kind.Neutral)
        => Output.SetStatus(text, kind);

    private void SetButtonsEnabled(bool enabled)
    {
        void Set()
        {
            StatusButton.IsEnabled = enabled;
            ApplyButton.IsEnabled = enabled;
            RevertButton.IsEnabled = enabled;
            PurgeButton.IsEnabled = enabled;
            DiffButton.IsEnabled = enabled;
        }
        if (Dispatcher.CheckAccess())
            Set();
        else
            Dispatcher.Invoke(Set);
    }
}
