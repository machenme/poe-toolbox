using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 浅色 / 深色令牌的 key 集合必须逐字一致。
///
/// <para>
/// 深色字典 <c>Tokens.Dark.xaml</c> 只放颜色，尺寸与圆角仍走浅色版（同一个 key 两个地方定义会
/// 互相盖掉）。代价是：少定义一个 key，深色下它会静默回落到 WPF 默认色——黑底黑字、
/// 边框消失这类问题只在切主题的那一瞬间暴露，靠肉眼发现太晚。本测试把它变成编译期失败。
/// </para>
/// </summary>
public sealed class ThemeTokenParityTests
{
    private static readonly Regex KeyPattern =
        new(@"x:Key\s*=\s*""(?<key>[A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    [Fact]
    public void DarkTokensCoverEveryColorKeyFromLightTokens()
    {
        var themes = Path.Combine(FindSourceRoot(), "PoEToolbox.Ui", "Themes");
        // 只比颜色 key：深色版刻意不重复定义圆角 / 字号 / 间距（那些与主题无关，
        // 两个地方各定义一次反而会互相盖掉）。
        var light = ReadKeys(Path.Combine(themes, "Tokens.xaml"))
            .Where(kv => IsColorKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var dark = ReadKeys(Path.Combine(themes, "Tokens.Dark.xaml"));

        Assert.NotEmpty(light);
        Assert.NotEmpty(dark);

        var missingInDark = light.Keys.Where(k => !dark.ContainsKey(k)).OrderBy(k => k).ToList();
        var extraInDark = dark.Keys.Where(k => !light.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(
            missingInDark.Count == 0 && extraInDark.Count == 0,
            "深色令牌与浅色令牌的颜色 key 不一致。\n"
            + "深色缺失：" + string.Join(", ", missingInDark) + "\n"
            + "深色多出：" + string.Join(", ", extraInDark));
    }

    /// <summary>
    /// 取「颜色类」key：带 <c>Brush</c> 后缀的。
    /// </summary>
    [Fact]
    public void DarkTokensOnlyDefineBrushesSoSharedMetricsHaveOneHome()
    {
        var themes = Path.Combine(FindSourceRoot(), "PoEToolbox.Ui", "Themes");
        var dark = ReadKeys(Path.Combine(themes, "Tokens.Dark.xaml"));

        var nonBrush = dark.Keys.Where(k => !k.EndsWith("Brush", StringComparison.Ordinal)).ToList();
        Assert.True(
            nonBrush.Count == 0,
            "Tokens.Dark.xaml 只应定义颜色令牌（*Brush），其余 key 请留在浅色版：\n"
            + string.Join("\n", nonBrush));
    }

    /// <summary>颜色令牌的判定：带 <c>Brush</c> 后缀。其余是尺寸类，两版共用一份定义。</summary>
    private static bool IsColorKey(string key) => key.EndsWith("Brush", StringComparison.Ordinal);

    private static Dictionary<string, string> ReadKeys(string path)
    {
        Assert.True(File.Exists(path), "找不到 " + path);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = File.ReadAllText(path);
        foreach (Match m in KeyPattern.Matches(text))
        {
            var key = m.Groups["key"].Value;
            // 重名 key 在同一字典里是后者覆盖前者；测试不比值，只比 key 集合。
            map[key] = key;
        }

        return map;
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
