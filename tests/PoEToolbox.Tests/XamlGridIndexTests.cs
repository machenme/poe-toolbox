using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// XAML 的 Grid 列/行索引越界守卫。
///
/// <para>
/// WPF 对超出 <c>Grid.ColumnDefinitions</c> 范围的 <c>Grid.Column</c> 既不报错也不抛异常——
/// 它当成「未指定」处理，把控件塞回第 0 列。若第 0 列是 <c>Width="Auto"</c>，
/// 控件宽度就由内容反向驱动，可能把Button 拉成横贯整行的一条色块。
/// </para>
///
/// <para>
/// 实测踩过：DashboardView 的客户端状态卡只声明了 2 列，按钮却写
/// <c>Grid.Column="2"</c>，深色下那条 <c>AccentBrush</c> 蓝按钮正好铺满整张卡，
/// 把状态文字全压住了。编译 0 错误、测试全绿，只有肉眼能发现。
/// </para>
/// </summary>
public class XamlGridIndexTests
{
    /// <summary>
    /// 每个带 Grid.Column / Grid.Row 的 Grid，声明的行列数必须覆盖用到的最大索引。
    /// </summary>
    [Theory]
    [InlineData("PoEToolbox.App", "DashboardView.xaml")]
    [InlineData("PoEToolbox.App", "MainWindow.xaml")]
    [InlineData("PoEToolbox.Ui", "PageHeader.xaml")]
    public void GridColumnAndRowIndexesStayInRange(string project, string fileName)
    {
        var path = Path.Combine(FindSourceRoot(), project, fileName);
        Assert.True(File.Exists(path), "找不到 XAML：" + path);

        var violations = FindOutOfRangeIndexes(File.ReadAllText(path));
        Assert.True(
            violations.Count == 0,
            $"{fileName} 有 Grid 索引越界（越界不报错，但控件会被塞回 0 列并被拉成整行色块）：\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>直接对已知会越界的片段断言，防止本测试自身写空转（写过一次假通过）。</summary>
    [Fact]
    public void DetectsOutOfRangeGridColumn()
    {
        const string Buggy = """
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <Button Grid.Column="2"/>
            </Grid>
            """;

        var violations = FindOutOfRangeIndexes(Buggy);

        Assert.Single(violations);
        Assert.Contains("只声明了 2 列", violations[0], StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", violations[0], StringComparison.Ordinal);
    }

    /// <summary>声明足够时不得误报。</summary>
    [Fact]
    public void AcceptsInRangeGridColumn()
    {
        const string Ok = """
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <Button Grid.Column="2"/>
            </Grid>
            """;

        Assert.Empty(FindOutOfRangeIndexes(Ok));
    }

    /// <summary>
    /// 找出所有「用了 Grid.Column=N / Grid.Row=N，但所在 Grid 声明不足 N+1 条」的违例。
    /// 做法：对每个 &lt;Grid…&gt; 配对出区间，在区间内数声明条数与使用处，两者对比。
    /// </summary>
    internal static List<string> FindOutOfRangeIndexes(string xaml)
    {
        var violations = new List<string>();

        var regionRegex = new Regex(@"<Grid(\s[^>]*?)?>");
        foreach (Match open in regionRegex.Matches(xaml))
        {
            var closeIndex = FindMatchingClose(xaml, open.Index);
            if (closeIndex < 0) continue;

            // 切片起点是本 Grid 的开标签，便于读声明段。
            var body = xaml[open.Index..closeIndex];
            var lineNo = xaml[..open.Index].Count(c => c == '\n') + 1;

            var cols = CountOwnDefinition(body, "ColumnDefinition", "Grid.ColumnDefinitions");
            var rows = CountOwnDefinition(body, "RowDefinition", "Grid.RowDefinitions");

            // 只在「本 Grid 显式声明了行列」时检查。缺声明意味着依赖父级或就是布局根，
            // 这种Grid 的内部索引由祖先决定，静态判断不可靠，直接跳过避免误报。
            if (cols == 0 && rows == 0) continue;

            var own = RemoveNestedGridBodies(body);

            // 列声明存在时才查列，行声明存在时才查行：Grid 可以只声明其中一种。
            if (cols > 0)
            {
                foreach (Match use in Regex.Matches(own, @"Grid\.Column\s*=\s*""(\d+)"""))
                {
                    var index = int.Parse(use.Groups[1].Value);
                    if (index >= cols)
                    {
                        violations.Add(
                            $"第 {lineNo} 行开始的 Grid 只声明了 {cols} 列，"
                            + $"但内部用了 Grid.Column=\"{index}\"（越界 {index - cols + 1} 列）");
                    }
                }
            }

            if (rows > 0)
            {
                foreach (Match use in Regex.Matches(own, @"Grid\.Row\s*=\s*""(\d+)"""))
                {
                    var index = int.Parse(use.Groups[1].Value);
                    if (index >= rows)
                    {
                        violations.Add(
                            $"第 {lineNo} 行开始的 Grid 只声明了 {rows} 行，"
                            + $"但内部用了 Grid.Row=\"{index}\"（越界 {index - rows + 1} 行）");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>数紧跟在本 Grid 开标签之后的第一段定义属性元素里，有多少条定义。</summary>
    /// <param name="itemName">定义条目的名字，不带尖括号，如 ColumnDefinition。</param>
    /// <param name="wrapperName">包裹它们的属性元素名，不带尖括号，如 Grid.ColumnDefinitions。</param>
    private static int CountOwnDefinition(string body, string itemName, string wrapperName)
    {
        var wrapper = body.IndexOf("<" + wrapperName, StringComparison.Ordinal);
        if (wrapper < 0) return 0;

        var end = body.IndexOf("</" + wrapperName + '>', wrapper, StringComparison.Ordinal);
        if (end < 0) return 0;

        var section = body[wrapper..end];
        return Regex.Matches(section, "<" + itemName + @"\b").Count;
    }

    /// <summary>
    /// 把 body 里所有嵌套 Grid 的区间挖掉，只留本层内容。
    /// body 的第 0 个字符就是本 Grid 的开标签，必须跳过它——
    /// 它身上的 Grid.Row/Grid.Column 是「被父级定位」，不是给子元素用的索引。
    /// </summary>
    private static string RemoveNestedGridBodies(string body)
    {
        var result = new StringBuilder();
        var cursor = 0;

        foreach (Match tag in GridTagRegex().Matches(body))
        {
            // 本层开标签：跳过，同时把它整段从结果里排除。
            if (tag.Index == 0)
            {
                result.Append(body, cursor, tag.Index - cursor);
                cursor = tag.Index + tag.Length;
                continue;
            }

            var text = tag.Value;
            if (text.StartsWith("</", StringComparison.Ordinal))
            {
                continue;                            // </Grid>
            }

            if (text.EndsWith("/>", StringComparison.Ordinal))
            {
                continue;                            // 自闭合，无子级
            }

            var close = FindMatchingClose(body, tag.Index);
            if (close < 0 || close < cursor)
            {
                continue;
            }

            result.Append(body, cursor, tag.Index - cursor);
            cursor = close;
        }

        result.Append(body, cursor, body.Length - cursor);
        return result.ToString();
    }

    /// <summary>从 &lt;Grid 开标签起，找到配对的 &lt;/Grid&gt; 的起始位置。</summary>
    private static int FindMatchingClose(string xaml, int openIndex)
    {
        var depth = 0;
        foreach (Match tag in GridTagRegex().Matches(xaml, openIndex))
        {
            // 关键：不能用 Group.Success 判断组是否参与匹配 —— 对 "(/)" 这种可空组，
            // .NET 在「参与了匹配但捕获空串」时Success 同样是 true。
            // 只能看 Value 是否真的有内容。
            var text = tag.Value;
            var isCloser = text.StartsWith("</", StringComparison.Ordinal);
            var isSelfClosing = text.EndsWith("/>", StringComparison.Ordinal);

            if (isSelfClosing)
            {
                return tag.Index == openIndex ? -1 : depth;
            }

            depth += isCloser ? -1 : 1;
            if (depth == 0)
            {
                return tag.Index;
            }
        }

        return -1;
    }

    private static Regex GridTagRegex() => new(@"<(/?)Grid(\s[^>]*?)?(/?)>");

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