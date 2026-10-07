using System.Text.RegularExpressions;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// MainWindow 构造函数的初始化顺序守卫。
///
/// <para>
/// 背景：Dashboard 首页是左导航的第一项。给<c>NavList.ItemsSource</c> 赋值时，
/// WPF 的 <c>Selector.SetSelectedToCurrent</c> 会自动选中第一项，立刻触发
/// <c>SelectionChanged</c> → <c>ShowDashboard()</c>。如果那时 <c>_dashboard</c>
/// 还没创建，就是启动即崩的 NullReferenceException（0.2.5 发布时踩过一次）。
/// </para>
/// </summary>
public class ShellInitOrderTests
{
    [Fact]
    public void DashboardIsBuiltBeforeNavItemsSourceIsAssigned()
    {
        var text = ReadMainWindow();

        var dashboard = text.IndexOf("_dashboard = new DashboardView", StringComparison.Ordinal);
        var itemsSource = text.IndexOf("NavList.ItemsSource =", StringComparison.Ordinal);

        Assert.True(dashboard >= 0, "构造函数里找不到 _dashboard = new DashboardView");
        Assert.True(itemsSource >= 0, "构造函数里找不到 NavList.ItemsSource 赋值");

        Assert.True(
            dashboard < itemsSource,
            "_dashboard 必须在 NavList.ItemsSource 赋值之前创建："
            + "赋值会自动选中第一项（Dashboard）并触发 SelectionChanged → ShowDashboard()，"
            + "那时 _dashboard 为 null 会抛 NullReferenceException。"
            + $"实际位置：_dashboard={dashboard}, ItemsSource={itemsSource}");
    }

    /// <summary>
    /// ShowDashboard 必须能扛住「控件还没准备好」的调用。
    /// 防御性守卫比只靠顺序更可靠——以后新增任何提前调用路径也不会崩。
    /// </summary>
    [Fact]
    public void ShowDashboardToleratesIncompleteInitialization()
    {
        var body = ExtractMethodBody(ReadMainWindow(), "private void ShowDashboard()");

        Assert.Contains("_dashboard is null", body, StringComparison.Ordinal);
        Assert.Contains("return;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 外壳页头由 MainWindow 统一托管：Dashboard 页必须把它收起，否则和 Dashboard 自带页头叠成两个标题。
    /// </summary>
    [Fact]
    public void ShowDashboardClearsShellPageHeader()
    {
        var body = ExtractMethodBody(ReadMainWindow(), "private void ShowDashboard()");

        Assert.Contains("ModuleHeader.IconGlyph = string.Empty", body, StringComparison.Ordinal);
        Assert.Contains("ModuleHeader.HeaderText = string.Empty", body, StringComparison.Ordinal);
        Assert.Contains("ModuleHeader.Description = string.Empty", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 每个模块页都要有页头，页头三项都必须被赋值。
    /// </summary>
    [Fact]
    public void ModuleHeaderIsPopulatedFromPluginMetadata()
    {
        var body = ExtractMethodBody(ReadMainWindow(), "private void ShowModuleHeader(");

        Assert.Contains("plugin.IconGlyph", body, StringComparison.Ordinal);
        Assert.Contains("UILabels.Get(plugin.Name)", body, StringComparison.Ordinal);
        Assert.Contains("plugin.Summary", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 每个 IUiPlugin 实现都得有 Summary，否则页头说明行是空的，Dashboard 卡片也少一句话。
    /// </summary>
    [Theory]
    [InlineData("AffixWorkbenchPlugin.cs")]
    [InlineData("BagCleanerPlugin.cs")]
    [InlineData("DataBrowserPlugin.cs")]
    [InlineData("MapNumberPlugin.cs")]
    [InlineData("FxPatchCreatorPlugin.cs")]
    [InlineData("FxPatchPlugin.cs")]
    [InlineData("Poe2FontPlugin.cs")]
    [InlineData("PoeCnPatchPlugin.cs")]
    [InlineData("PriceTaggerPlugin.cs")]
    [InlineData("TermTranslatorPlugin.cs")]
    [InlineData("VoyagerPlugin.cs")]
    public void EveryPluginDeclaresSummary(string fileName)
    {
        var path = FindPluginFile(fileName);
        Assert.True(File.Exists(path), "找不到插件源文件：" + path);

        var text = File.ReadAllText(path);
        Assert.Contains("public string Summary =>", text, StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string ReadMainWindow()
        => File.ReadAllText(Path.Combine(
            FindSourceRoot(), "PoEToolbox.App", "MainWindow.xaml.cs"));

    /// <summary>取方法体：从方法签名起，到下一个同缩进的成员声明为止。</summary>
    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到方法：" + signature);

        // 同缩进（4 空格）的下一个 "    private "/"    public "/"}" 之前就是方法体。
        var tail = source[start..];
        var end = Regex.Match(tail[signature.Length..], @"\n    (?:private|public|internal|protected) ");
        return end.Success ? tail[..(signature.Length + end.Index)] : tail;
    }

    private static string FindPluginFile(string fileName)
    {
        var src = FindSourceRoot();
        return Directory.GetFiles(src, fileName, SearchOption.AllDirectories).FirstOrDefault()
            ?? Path.Combine(src, "(未找到)", fileName);
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