using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.PoeCnPatch;

public partial class PoeCnPatchView : UserControl
{
    private const string Pob1DownloadUrl =
        "https://v4.gh-proxy.org/https://github.com/PathOfBuildingCommunity/PathOfBuilding/releases/latest/download/PathOfBuildingCommunity-Portable.zip";
    private const string Pob2DownloadUrl =
        "https://v4.gh-proxy.org/https://github.com/PathOfBuildingCommunity/PathOfBuilding-PoE2/releases/latest/download/PathOfBuildingCommunity-PoE2-Portable.zip";
    private bool _isInitialized;
    private PobGame _selectedGame = PobGame.Poe1;

    public PoeCnPatchView()
    {
        InitializeComponent();
        TargetFiles.ItemsSource = new[] { "TreeTab.lua", "TradeQuery.lua", "TradeQueryGenerator.lua" };
        _isInitialized = true;
        UpdatePreview();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 PoB 项目目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            TreeTabPathBox.Text = dialog.FolderName;
            ValidatePath();
        }
    }

    private void SeasonPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || radio.Tag is not string season) return;
        SeasonBox.Text = season;
        UpdatePreview();
    }

    private void SeasonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized) UpdatePreview();
    }

    private void TargetPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized) ValidatePath();
    }

    private void PobGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
            _selectedGame = string.Equals(tag, "POE2", StringComparison.OrdinalIgnoreCase)
                ? PobGame.Poe2
                : PobGame.Poe1;

        ValidatePath();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var valid = PatchService.TryNormalizeSeason(SeasonBox?.Text ?? string.Empty, out var season);
        var league = valid ? PatchService.BuildLeague(season) : string.Empty;
        PreviewSeason.Text = valid ? league : "无效";
        PreviewSeason.Foreground = FindResource(valid ? "TextPrimaryBrush" : "ErrorBrush") as System.Windows.Media.Brush;
        PreviewUrl.Text = valid
            ? $"https://poe.game.qq.com/trade/search/{Uri.EscapeDataString(league)}/..."
            : "请输入 S30 或 30 这样的赛季编号";
        SeasonStatus.Text = valid ? $"将写入 {league}。" : "只支持 S 加数字或纯数字，例如 S30、30。";
        SeasonStatus.Foreground = FindResource(valid ? "SuccessBrush" : "ErrorBrush") as System.Windows.Media.Brush;
        ApplyButton.IsEnabled = valid && PatchService.TryGetTargets(
            TreeTabPathBox?.Text.Trim() ?? "", _selectedGame, out _);
        if (TreeTabPathBox is not null) ValidatePath(false);
    }

    private void ValidatePath(bool updateStatus = true)
    {
        var path = TreeTabPathBox.Text.Trim();
        var valid = PatchService.TryGetTargets(path, _selectedGame, out var targets);
        if (updateStatus)
        {
            PathStatus.Text = valid
                ? $"已找到三个补丁文件：{Path.GetDirectoryName(targets[0])}"
                : "请选择 PoB 项目目录，并确保 TreeTab.lua 与两个 TradeQuery 文件完整。";
            PathStatus.Foreground = FindResource(valid ? "SuccessBrush" : "WarningBrush") as System.Windows.Media.Brush;
        }

        ApplyButton.IsEnabled = valid && PatchService.TryNormalizeSeason(SeasonBox.Text, out _);
        RestoreButton.IsEnabled = valid;
        if (valid)
        {
            TargetFiles.ItemsSource = targets.Select(Path.GetFileName).ToArray();
            ResultText.Text = "文件结构已就绪，可以应用补丁。";
        }
        else
        {
            TargetFiles.ItemsSource = new[] { "TreeTab.lua", "TradeQuery.lua", "TradeQueryGenerator.lua" };
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var targetPath = TreeTabPathBox.Text.Trim();
        if (!PatchService.TryNormalizeSeason(SeasonBox.Text, out var season))
        {
            MessageBox.Show("请输入 S30 或 30 这样的赛季编号。", "赛季无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!PatchService.TryGetTargets(targetPath, _selectedGame, out _))
        {
            MessageBox.Show("请选择正确的 PoB 项目目录，并确保三个 Lua 文件完整。", "文件不完整",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"将把交易站切换为 poe.game.qq.com，并使用赛季 {PatchService.BuildLeague(season)}。\n发生变化的文件会先创建本地备份，是否继续？",
            "应用国服补丁", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var result = PatchService.Apply(targetPath, _selectedGame, season);
            UiStatus.Set(StatusText, result.ChangedFiles.Count == 0
                ? $"文件已经是 {result.League} 配置。"
                : $"✅ 补丁完成：已切换到 {result.League}。", UiStatus.Kind.Success);
            FileLogger.App.Info($"PoeCnPatch applied: game={_selectedGame}, league={result.League}, changed={result.ChangedFiles.Count}.");
            ResultText.Text = result.ChangedFiles.Count == 0
                ? "没有需要修改的内容。"
                : $"已生成备份：{string.Join(", ", result.BackupFiles.Select(Path.GetFileName))}";
            ValidatePath();
        }
        catch (Exception ex)
        {
            UiStatus.Set(StatusText, "❌ 补丁失败。", UiStatus.Kind.Error);
            FileLogger.App.Error("PoeCnPatch apply failed.", ex);
            ResultText.Text = ex.Message;
            MessageBox.Show(ex.Message, "补丁失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var targetPath = TreeTabPathBox.Text.Trim();
        if (!PatchService.TryGetTargets(targetPath, _selectedGame, out _))
        {
            MessageBox.Show("请选择正确的 PoB 项目目录，并确保三个 Lua 文件完整。", "文件不完整",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "将优先使用本地备份恢复国际服设置；没有备份时仅恢复交易站域名。是否继续？",
            "还原国际服", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var result = PatchService.Restore(targetPath, _selectedGame);
            UiStatus.Set(StatusText, result.RestoredFiles.Count == 0
                ? "文件已经是国际服状态。"
                : "✅ 已恢复国际服设置。", UiStatus.Kind.Success);
            FileLogger.App.Info($"PoeCnPatch restored: restored={result.RestoredFiles.Count}.");
            ResultText.Text = result.RestoredFiles.Count == 0
                ? "没有需要恢复的内容。"
                : result.UsedBackups
                    ? $"已从本地备份还原：{string.Join(", ", result.RestoredFiles.Select(Path.GetFileName))}"
                    : $"未找到本地备份，已恢复交易站域名：{string.Join(", ", result.RestoredFiles.Select(Path.GetFileName))}";
            ValidatePath();
        }
        catch (Exception ex)
        {
            UiStatus.Set(StatusText, "❌ 还原失败。", UiStatus.Kind.Error);
            FileLogger.App.Error("PoeCnPatch restore failed.", ex);
            ResultText.Text = ex.Message;
            MessageBox.Show(ex.Message, "还原失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DownloadPob1_Click(object sender, RoutedEventArgs e)
        => OpenDownloadUrl(Pob1DownloadUrl);

    private void DownloadPob2_Click(object sender, RoutedEventArgs e)
        => OpenDownloadUrl(Pob2DownloadUrl);

    private static void OpenDownloadUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开下载链接：\n{ex.Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
