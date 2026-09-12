using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

public partial class FxPatchView : UserControl
{
    private bool _running;

    public FxPatchView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await AutoDetectGamePathAsync();
    }

    // ═══ 游戏路径 ═══════════════════════════════════════════════
    private async Task AutoDetectGamePathAsync()
    {
        if (GameDataPathBox.Text.Length > 0)
            return;
        SetStatus("正在自动检测游戏数据……");
        try
        {
            var path = await Task.Run(() => GameDataLoader.ResolvePath(null));
            GameDataPathBox.Text = path;
            SetStatus("已自动检测到游戏数据。");
        }
        catch
        {
            SetStatus("未自动检测到游戏数据，请手动选择。");
        }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择游戏数据",
            Filter = "游戏数据 (Content.ggpk;_.index.bin)|Content.ggpk;_.index.bin|全部文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            GameDataPathBox.Text = dlg.FileName;
    }

    private void BrowsePatch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择补丁描述文件", Filter = "补丁描述 (*.patch.json;*.json;*.zip)|*.patch.json;*.json;*.zip|全部文件 (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
            PatchJsonBox.Text = dlg.FileName;
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

    private void PatchSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 初始化期间首项 IsSelected 会先于其他控件创建触发本事件，需空值防护
        if (PatchJsonBox is null || BrowsePatchButton is null)
            return;
        var isCustom = PatchSourceBox.SelectedIndex == 1;
        PatchJsonBox.IsEnabled = isCustom;
        BrowsePatchButton.IsEnabled = isCustom;
    }

    // ═══ 引擎调用 ═══════════════════════════════════════════════
    private bool ValidateInputs(out string gameData, out string? patchJson)
    {
        gameData = GameDataPathBox.Text.Trim();
        patchJson = null;
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
        await RunEngineAsync("查看状态", builtIn, args);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var gameData, out var patchJson))
            return;
        var builtIn = patchJson is null;
        var args = builtIn
            ? new[] { gameData, "apply" }
            : new[] { gameData, patchJson!, "apply" };
        await RunEngineAsync("应用补丁", builtIn, args);
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var gameData, out var patchJson))
            return;
        var builtIn = patchJson is null;
        var args = builtIn
            ? new[] { gameData, "revert" }
            : new[] { gameData, patchJson!, "revert" };
        await RunEngineAsync("还原补丁", builtIn, args);
    }

    private async void Diff_Click(object sender, RoutedEventArgs e)
    {
        var vanilla = DiffVanillaBox.Text.Trim();
        var modified = DiffModifiedBox.Text.Trim();
        if (vanilla.Length == 0 || modified.Length == 0 || !File.Exists(vanilla) || !File.Exists(modified))
        {
            SetStatus("请选择有效的原版索引与修改后索引。", UiStatus.Kind.Warning);
            return;
        }
        var patchId = $"diff-{DateTime.Now:yyyyMMdd-HHmm}";
        // 与引擎默认一致：生成物落 %LOCALAPPDATA%\PoEToolbox\patches\<patchId>\
        var outDir = Path.Combine(ConfigService.PatchesDirectory, patchId);
        var args = new[]
        {
            "diff", vanilla, modified,
            "-o", outDir,
            "--id", patchId,
            "--bundle", "DiffPatch",
        };
        await RunEngineAsync($"生成补丁（输出到 {outDir}）", builtIn: false, args, skipGameData: true);
    }

    private async Task RunEngineAsync(string action, bool builtIn, string[] args, bool skipGameData = false)
    {
        if (_running)
        {
            SetStatus("已有任务在执行，请稍候。", UiStatus.Kind.Warning);
            return;
        }
        if (!skipGameData && GameDataPathBox.Text.Trim().Length > 0 && File.Exists(GameDataPathBox.Text.Trim()) is false && Directory.Exists(GameDataPathBox.Text.Trim()) is false)
        {
            SetStatus("游戏数据路径无效。", UiStatus.Kind.Warning);
            return;
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
        }
        else
        {
            SetStatus($"❌ {action}失败（退出码 {exitCode}），详见下方引擎输出。", UiStatus.Kind.Error);
            FileLogger.App.Error($"UI {action}: failed with exit code {exitCode}.");
            AppendLog("");
            AppendLog($"════════════ ❌ {action}失败（退出码 {exitCode}）════════════");
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
            DiffButton.IsEnabled = enabled;
        }
        if (Dispatcher.CheckAccess())
            Set();
        else
            Dispatcher.Invoke(Set);
    }
}
