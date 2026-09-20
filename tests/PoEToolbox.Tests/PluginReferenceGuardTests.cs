using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 不变式 12（docs/ARCHITECTURE.md §4）：插件工程之间不许互相引用。
/// 这条是报告 P1-5 一次重构才换来的（`Voyager → BagCleaner` 拆掉，纯 Win32/GDI 类型下沉 `Core`，
/// 热键契约提到 `Abstractions` 由组合根注入）。但当时没有任何东西把它变成机器可查的约束：
/// 往插件 csproj 里加回一行 ProjectReference，编译得过、CI 两条腿照样绿。本测试补的就是这道护栏。
/// </summary>
public sealed class PluginReferenceGuardTests
{
    private static readonly Regex ProjectReferencePattern =
        new(@"<ProjectReference\s+Include=""([^"")]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void NoPluginProjectReferencesAnotherPluginProject()
    {
        var sourceRoot = FindSourceRoot();
        var offenders = new List<string>();

        foreach (var pluginDirectory in Directory.EnumerateDirectories(sourceRoot, "PoEToolbox.Plugins.*"))
        {
            var self = Path.GetFileName(pluginDirectory) + ".csproj";
            var project = Path.Combine(pluginDirectory, self);
            if (!File.Exists(project))
            {
                continue;
            }

            foreach (var target in ReadProjectReferences(project))
            {
                if (!target.StartsWith("PoEToolbox.Plugins.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!target.Equals(self, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{self} → {target}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "插件工程之间出现了直接引用：" + string.Join("；", offenders) + "。要复用别的插件里的东西只有两条路——"
            + "类型本身是纯 Win32/GDI/领域逻辑就下沉到 Core，或者留在原插件、把契约提到 Abstractions 用工厂倒置、"
            + "由组合根 PluginManager 注入。见 docs/ARCHITECTURE.md §4 不变式 12。");
    }

    private static IEnumerable<string> ReadProjectReferences(string projectFile) =>
        ProjectReferencePattern.Matches(File.ReadAllText(projectFile))
            .Select(match => Path.GetFileName(match.Groups[1].Value.TrimEnd('\\')))
            .Where(name => name.Length > 0);

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
            "从 " + AppContext.BaseDirectory + " 往上找不到 src/PoEToolbox.Plugins.AffixWorkbench，"
            + "本测试需要源码树在位（CI 与本地 dotnet test 都满足）。");
    }
}
