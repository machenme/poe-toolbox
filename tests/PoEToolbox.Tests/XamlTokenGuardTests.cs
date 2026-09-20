using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// UI 重铸护栏（SPEC-ui-rebuild.md §步骤3）：视图 XAML 不得出现字面色值。
/// 颜色的唯一真相在 src/PoEToolbox.Ui/Themes/Tokens.xaml，页面只允许通过
/// DynamicResource / StaticResource 引用语义令牌与组件 key。字面 hex 一旦
/// 回流，改一处色板就要重新逐页搜索替换，重铸的收益会被稀释归零。
/// </summary>
public sealed class XamlTokenGuardTests
{
    private static readonly Regex HexColorPattern =
        new(@"#[0-9A-Fa-f]{6,8}", RegexOptions.Compiled);

    [Fact]
    public void ViewXamlContainsNoRawHexColors()
    {
        var sourceRoot = FindSourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/"))
            {
                continue;
            }

            if (normalized.Contains("/Themes/"))
            {
                // Tokens / Typography / Controls.* 是令牌与组件样式的定义处，
                // 是全库唯一允许出现字面色值的地方。
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (HexColorPattern.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "视图 XAML 里出现了字面色值（应改引用 PoEToolbox.Ui/Themes 的语义令牌）：\n"
            + string.Join("\n", offenders));
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
