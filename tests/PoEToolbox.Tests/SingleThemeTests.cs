using System.IO;
using System.Linq;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 单一浅色主题的守卫。
///
/// <para>
/// 2026-10-07 决定：移除深浅色切换，界面固定浅色。原因是这套颜色是按 PoE 金 +
/// Fluent 蓝重新规划的，深色版需要另配一套色板才能保证对比度，而实际使用中
/// 深色需求不强，却让每次改配色都要双份同步、且切主题时按钮会短暂褪色。
/// </para>
///
/// <para>
/// 这些测试防止深色主题被无意中加回来——它牵扯注册表监听、MergedDictionaries
/// 替换、以及326 处 StaticResource 样式引用不能动的约束，成本很高，不能悄悄复活。
/// </para>
/// </summary>
public sealed class SingleThemeTests
{
    [Fact]
    public void NoDarkTokenDictionaryExists()
    {
        var themes = Path.Combine(FindSourceRoot(), "PoEToolbox.Ui", "Themes");

        var dark = Directory.EnumerateFiles(themes, "*.xaml")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("Dark", System.StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Theme.", System.StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        Assert.True(
            dark.Count == 0,
            "已定为单一浅色主题，不该再有深色/主题切换用的令牌字典：\n  " + string.Join("\n  ", dark));
    }

    [Fact]
    public void ThemeServiceIsGone()
    {
        var service = Path.Combine(FindSourceRoot(), "PoEToolbox.Ui", "ThemeService.cs");

        Assert.False(
            File.Exists(service),
            "ThemeService 是运行时读注册表判断深色的那套机制，已随深色主题一并移除。");
    }

    /// <summary>
    /// 外壳不能有主题下拉框，也不能再调ThemeService。
    /// </summary>
    [Fact]
    public void ShellHasNoThemeSwitcher()
    {
        var shell = Path.Combine(FindSourceRoot(), "PoEToolbox.App", "MainWindow.xaml.cs");
        var shellXaml = Path.Combine(FindSourceRoot(), "PoEToolbox.App", "MainWindow.xaml");

        Assert.DoesNotContain("ThemeService", File.ReadAllText(shell), System.StringComparison.Ordinal);
        Assert.DoesNotContain("ThemeCombo", File.ReadAllText(shellXaml), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 颜色令牌必须并回 DesignSystem：App.xaml 不再单独持有颜色层
    /// （那一层原本是留给 ThemeService 整层替换的）。
    /// </summary>
    [Fact]
    public void ColorsLiveInsideDesignSystemAgain()
    {
        var designSystem = Path.Combine(FindSourceRoot(), "PoEToolbox.Ui", "Themes", "DesignSystem.xaml");
        var text = File.ReadAllText(designSystem);

        Assert.Contains("Themes/Tokens.xaml", text, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 品牌金必须存在，且交互色仍是 Fluent 蓝——#C89B3C 在白底上对比度约 2.4:1，
    /// 直接拿来做选中态会看不清，所以Accent 系列保持蓝色。
    /// </summary>
    [Fact]
    public void BrandGoldAndAccentBlueBothDefined()
    {
        var tokens = File.ReadAllText(
            Path.Combine(FindSourceRoot(), "PoEToolbox.Ui", "Themes", "Tokens.xaml"));

        foreach (var key in new[]
                 {
                     "BrandBrush", "BrandSubtleBrush", "BrandBorderBrush", "BrandForegroundBrush",
                 })
        {
            Assert.Contains($"x:Key=\"{key}\"", tokens, System.StringComparison.Ordinal);
        }

        Assert.Contains("#C89B3C", tokens, System.StringComparison.Ordinal);
        Assert.Contains("#0F6CBD", tokens, System.StringComparison.Ordinal);
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var source = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(Path.Combine(source, "PoEToolbox.Plugins.AffixWorkbench")))
            {
                return source;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "从 " + AppContext.BaseDirectory + " 往上找不到 src，本测试需要源码树在位。");
    }
}