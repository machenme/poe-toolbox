using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PoEToolbox.Tests;

/// <summary>
/// 内容区尺寸守卫：模块必须真的铺满左导航右侧的可用空间——<b>宽和高都要量</b>。
///
/// <para>
/// 2026-10-07 踩的坑（横向）：内容区写成
/// <c>&lt;Grid MaxWidth="1180" HorizontalAlignment="Center"&gt;</c>，
/// 注释里写着「居中并限制最大宽度：窗口拉宽时不至于让一行文字横跨两千像素」。
/// 看上去是「居中 + 封顶」，实际行为完全不同：
/// </para>
/// <list type="number">
/// <item><c>HorizontalAlignment="Center"</c> 让这一层<b>按内容的 DesiredSize 收缩</b>，
/// 而不是填满父容器给的可用宽度。</item>
/// <item><c>MaxWidth</c> 只是上限，<b>不拉满</b>。内容窄的时候它完全不生效。</item>
/// </list>
///
/// <para>
/// 实测（窗口 1546、左导航 264 ⇒ 内容区应得 1282）：这一层只拿到 <b>6px</b>，
/// 页头和插件视图一起被压回内容自然宽度，模块右侧空掉一大片。
/// 真机截图里「攻略翻译」的内容块只有 674px 宽、且两栏（源码是 <c>* / 12 / *</c>）
/// 实测 78px 与 582px 悬殊不等宽 —— 都是这个收缩造成的。
/// 改成 <c>HorizontalAlignment="Stretch"</c> 后同一场景实测 1282px，铺满。
/// </para>
///
/// <para>
/// <b>为什么必须真跑 WPF 布局</b>：这个 bug 里 <c>MaxWidth</c> 和
/// <c>HorizontalAlignment</c> 两个属性都「写得对」，正则读源码看不出问题；
/// 只有真的 Measure/Arrange 一遍、读 <see cref="FrameworkElement.ActualWidth"/>
/// 才知道它到底拿到了多少宽度。脚手架复用 <c>XamlLoadTests</c> 的 STA 方式。
/// </para>
///
/// <para>
/// ⭐ <b>横纵是两个独立问题，只测一个轴会漏</b>（同日第二个 bug）。横向修好后，
/// 内层 Grid 仍因<b>没写 <c>Grid.Row="1"</c></b> 默认落在 <c>Row 0</c>（Auto），
/// 纵向只拿到 15px、下半屏空出约 960px。当时本文件只有宽度断言，
/// 351 条全绿却完全没覆盖到它。<c>HorizontalAlignment</c> 也不会带上
/// <c>VerticalAlignment</c>，加 <c>VerticalAlignment="Stretch"</c> 无效。
/// ⇒ 探针必须宽高都极小，断言必须两个方向都写。
/// </para>
/// </summary>
public sealed class ContentAreaWidthTests
{
    private readonly ITestOutputHelper _out;
    public ContentAreaWidthTests(ITestOutputHelper output) => _out = output;

    /// <summary>左导航列宽，写在 <c>MainWindow.xaml</c> 的 Body Grid 上。</summary>
    private const double NavWidth = 264;

    /// <summary>与 <c>MainWindow.xaml</c> 的 <c>Width="1560"</c> 保持一致量级即可，
    /// 断言只关心「铺满」这个相对关系，不关心绝对像素。</summary>
    private const double WindowWidth = 1546;

    private const double WindowHeight = 993;

    /// <summary>
    /// 插件视图容器必须铺满内容区。探针用 <see cref="Border"/> 这种
    /// DesiredSize 极小的元素 —— 越窄的内容越容易暴露「被 DesiredSize 收缩」，
    /// 用真实插件视图反而可能因为内容本身够宽而侥幸通过。
    /// </summary>
    [Fact]
    public void PluginContentFillsAvailableWidth()
    {
        var (content, available) = MeasureShell();

        _out.WriteLine($"内容区可用宽度 = {available:F0}，PluginContent 实得 = {content:F0}");
        // 差 1px 容差：WPF 的 Arrange 有亚像素舍入，卡死会误报。
        Assert.True(content >= available - 1,
            $"插件视图只拿到 {content:F0}px，内容区有 {available:F0}px 可用，"
            + $"右侧空掉 {available - content:F0}px。"
            + "检查内容区那一层是不是误用了 HorizontalAlignment=\"Center\""
            + "（Center 会让这一层按内容 DesiredSize 收缩，MaxWidth 只是上限、不拉满）。");
    }

    /// <summary>
    /// 页头与插件视图是同一层的两行，宽度必须一致。
    /// 页头窄而视图宽（或反过来）都说明这一层的对齐方式不对。
    /// </summary>
    [Fact]
    public void PageHeaderMatchesContentWidth()
    {
        var (header, content) = MeasureHeaderAndContent();

        Assert.True(Math.Abs(header - content) <= 1,
            $"页头宽 {header:F0}px 与插件视图宽 {content:F0}px 不一致 —— "
            + "两者是同一层的两行，出现差异说明这一层的对齐方式不对。");
    }

    /// <summary>
    /// 插件视图必须占满内容区的<b>高度</b>，不能被压成内容自然高度。
    ///
    /// <para>
    /// 2026-10-07 第二个内容区 bug（主人截图：模块下半部分空出约 960px）。
    /// 这次横向是好的（1282/2240 都铺满了），但纵向只拿到 15px。
    /// </para>
    ///
    /// <para>
    /// 根因是 WPF 的一个非常隐蔽的默认值：<b>父 Grid 有 Row0=Auto / Row1=* 时，
    /// 没写 <c>Grid.Row</c> 的子元素默认落在第 0 行</b>，于是被 Auto 行压成
    /// 内容的 DesiredSize。外层 <c>ContentColumn</c> 明明分给了 1230px，
    /// 内层 Grid 却只拿到 15px（探针内容高）。
    /// </para>
    ///
    /// <para>
    /// 最小复现（脱离项目树，证明与项目代码无关）：
    /// 内层不写 <c>Grid.Row</c> ⇒ 15px；写 <c>Grid.Row="1"</c> ⇒ 1230px。
    /// </para>
    ///
    /// <para>
    /// <b>为什么上一轮的守卫没抓到</b>：只断言了宽度（<c>PluginContentFillsAvailableWidth</c>），
    /// 纵向一条断言都没有 ⇒ 横向修好后测试全绿，纵向这个洞完全没被覆盖。
    /// 教训：布局守卫必须<b>宽高两个方向都量</b>。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1100, 900)]     // 小窗：纵向同样不许留空
    [InlineData(1920, 1080)]
    [InlineData(2560, 1392)]    // 主人截图时的分辨率
    [InlineData(3840, 2160)]    // 4K
    public void PluginContentFillsAvailableHeight(int windowWidth, int windowHeight)
    {
        var (content, available) = MeasureShellHeight(windowWidth, windowHeight);

        // 探针内容只有 ~15px 高，铺满时 content 应该接近 available。
        // 留 4px 容差：页头 Margin 与亚像素舍入。
        Assert.True(content >= available - 4,
            $"窗口 {windowWidth}×{windowHeight} 时内容区可用高度 {available:F0}px，"
            + $"插件视图只拿到 {content:F0}px，底部空出 {available - content:F0}px。"
            + "检查内容区那几层 Grid：父 Grid 有 Row0=Auto / Row1=* 时，"
            + "内层若没写 Grid.Row=\"1\" 就会默认落在第 0 行，被 Auto 压成内容自然高度。");
    }

    /// <summary>
    /// 页头与插件视图<b>高度之和</b>应等于内容区可用高度，中间不许有断层。
    /// 页头是 Auto（按内容高），视图吃剩余——两者相加才是满。
    /// </summary>
    [Fact]
    public void HeaderAndContentHeightsAddUpToAvailable()
    {
        var (header, content, available) = MeasureHeaderContentHeight(WindowWidth, WindowHeight);

        Assert.True(Math.Abs(header + content - available) <= 4,
            $"页头 {header:F0} + 视图 {content:F0} = {header + content:F0}，"
            + $"但内容区可用 {available:F0}px —— 差了 {available - header - content:F0}px，"
            + "说明有元素没被拉伸到剩余高度。");
    }

    /// <summary>
    /// 在 STA 线程内量出插件视图的宽与高。
    ///
    /// <para>
    /// 只返回 double 不返回元素：元素归 STA 线程所有，xunit 断言跑在另一个线程上，
    /// 在那边读 <c>ActualWidth</c> 会抛「调用线程无法访问此对象，因为另一个线程拥有该对象」。
    /// </para>
    /// </summary>
    private static (double Content, double Available) MeasureHeaderAndContent()
    {
        return RunOnStaThread(() =>
        {
            var content = MeasureShellCore(WindowWidth, WindowHeight, out var header, out _, out _);
            return (header.ActualWidth, content.ActualWidth);
        });
    }

    private static (double Content, double Available) MeasureShellHeight(double w, double h)
        => RunOnStaThread(() =>
        {
            var content = MeasureShellCore(w, h, out _, out _, out var availH);
            return (content.ActualHeight, availH);
        });

    private static (double Header, double Content, double Available) MeasureHeaderContentHeight(double w, double h)
        => RunOnStaThread(() =>
        {
            var content = MeasureShellCore(w, h, out var header, out _, out var availH);
            return (header.ActualHeight, content.ActualHeight, availH);
        });

    /// <summary>
    /// 铺满的同时，超宽屏上仍要保留封顶，不能让一行文字横跨三千像素。
    ///
    /// <para>
    /// 这条锁的是「Stretch + 按比例封顶」的组合：只留 Stretch 会让超宽屏行长失控，
    /// 只留写死的 <c>MaxWidth</c> 又会退回「分辨率越高浪费越多」的老问题（见下一个测试）。
    /// </para>
    /// </summary>
    [Fact]
    public void WideWindowStaysWithinMaxWidth()
    {
        const double wide = 3840;   // 4K
        var (content, available) = MeasureShell(wide);

        Assert.True(content < available,
            $"窗口 {wide:F0}px 宽时内容区可用 {available:F0}px，"
            + $"插件视图也是 {content:F0}px —— 没有封顶，"
            + "文字行会横跨三千多像素。检查内容区那一层还保不保留 MaxWidth。");
    }

    /// <summary>
    /// 封顶必须<b>按比例</b>，不能是写死的绝对像素值。
    ///
    /// <para>
    /// 主人原话：「用户可能有不同的分辨率这样。如果写成固定数值可能出问题。」
    /// 写死 <c>MaxWidth="1600"</c> 的实测后果（内容区可用 → 实际浪费）：
    /// </para>
    /// <list type="bullet">
    /// <item>2560 → 2296 → 浪费 696px（<b>30.3%</b>）</item>
    /// <item>3440 → 3176 → 浪费 1576px（<b>49.6%</b>）</item>
    /// </list>
    ///
    /// <para>
    /// 所以这条钉住「浪费比例」而不是「绝对宽度」：任何分辨率下浪费都得是
    /// 同一量级的小比例。现在实测 2560→2.4%、3840→4.4%，收敛良好。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(3440)]
    [InlineData(3840)]
    public void WasteStaysProportionalOnLargeScreens(int windowWidth)
    {
        var (content, available) = MeasureShell(windowWidth);
        var wasteRatio = (available - content) / available;

        // 10% 是 generous 的上限：写死 1600 时 3440 上是 49.6%，
        // 无脑乘 0.92 时 1546 上是 8%（连舒适区都不铺满）。
        // 现在的分段策略实测最大 4.4%，留足余量又不会被"调比例"轻易放宽。
        Assert.True(wasteRatio < 0.10,
            $"窗口 {windowWidth:F0}px 时浪费 {wasteRatio:P1}"
            + $"（可用 {available:F0} / 实得 {content:F0}）。"
            + "封顶值应是可用宽度的比例，不能是写死的绝对像素 —— "
            + "写死会让高分辨率用户浪费一半屏幕。");
    }

    /// <summary>
    /// 舒适区（可用宽度 ≤ 1600，也就是 1080p 及以下）必须<b>完全铺满</b>。
    ///
    /// <para>
    /// 这是主人已经目视确认过的观感，不能为了照顾 4K 把它改掉。
    /// 试过「无脑乘 0.92」，结果主人窗口（可用 1282）也要浪费 103px —— 那是回退。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1100)]
    [InlineData(1280)]
    [InlineData(1546)]
    public void ComfortableWindowFillsEntirely(int windowWidth)
    {
        var (content, available) = MeasureShell(windowWidth);

        // 1px 容差：WPF Arrange 有亚像素舍入。
        Assert.True(content >= available - 1,
            $"窗口 {windowWidth:F0}px（可用 {available:F0}）时实得 {content:F0}，"
            + "舒适区内应当完全铺满。检查 ContentMaxWidthConverter.ComfortWidth "
            + "是不是被调小了 —— 它是「从哪儿开始收窄」的转折点，不是收窄上限。");
    }

    /// <summary>
    /// 搭一个只做过 Measure/Arrange 的外壳，返回 <c>PluginContent</c> 与内容区可用宽度。
    ///
    /// <para>
    /// 关键点：<b>不能 Show 窗口</b>，测试进程里 Show 会真建出原生窗口；
    /// 而且没 Show 过时 <c>Window.Measure</c> 不会触发内容布局，实测全是 0。
    /// 直接对 <c>Window.Content</c> 这棵根树做 Measure/Arrange 才拿得到真实宽度。
    /// </para>
    /// </summary>
    private static (double Content, double Available) MeasureShell(double width = WindowWidth)
        => RunOnStaThread(() =>
        {
            var content = MeasureShellCore(width, WindowHeight, out _, out var available, out _);
            return (content.ActualWidth, available);
        });

    /// <summary>
    /// 在 STA 线程上把外壳跑一遍布局，返回 <c>PluginContent</c> 与内容区可用宽/高。
    ///
    /// <para>
    /// 关键点一：<b>不能 Show 窗口</b>，测试进程里 Show 会真建出原生窗口；
    /// 而且没 Show 过时 <c>Window.Measure</c> 不会触发内容布局，实测全是 0。
    /// 直接对 <c>Window.Content</c> 这棵根树做 Measure/Arrange 才拿得到真实尺寸。
    /// </para>
    ///
    /// <para>
    /// 关键点二：<b>只能在 STA 线程内读元素属性</b>。返回的 <c>DependencyObject</c>
    /// 归创建它的线程所有，xunit 断言在别的线程上读 <c>ActualWidth</c> 会抛
    /// 「调用线程无法访问此对象」。所以元素只在内部流转，只把 double 带出去。
    /// </para>
    ///
    /// <para>
    /// 关键点三：探针内容<b>宽高都要极小</b>。宽 probe 抓「被 DesiredSize 收缩（横向）」，
    /// 高 probe 抓纵向那类。只用宽探针会漏掉纵向 bug —— 2026-10-07 就是这么漏的。
    /// </para>
    /// </summary>
    private static FrameworkElement MeasureShellCore(
        double width, double height,
        out FrameworkElement header,
        out double availableWidth,
        out double availableHeight)
    {
        // pack:// 的 UriParser 必须显式注册，否则 new Uri("pack://application:,,,/...")
        // 会把 ",,," 当端口号抛 UriFormatException。WPF 应用启动时会替你注册，
        // 裸测试进程没人做这件事。重复注册会抛，所以先查再注册。
        if (!UriParser.IsKnownScheme("pack"))
            UriParser.Register(new GenericUriParser(GenericUriParserOptions.GenericAuthority), "pack", -1);

        // 与 App.xaml 里的顺序一致：设计系统先于任何视图装载，
        // 否则视图里的 {StaticResource} 会找不到键。
        var designSystemUri = new Uri(
            "pack://application:,,,/PoEToolbox.Ui;component/Themes/DesignSystem.xaml",
            UriKind.Absolute);
        var app = Application.Current ?? new Application();
        if (!app.Resources.MergedDictionaries
                .Any(d => d.Source?.OriginalString == designSystemUri.OriginalString))
        {
            app.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = designSystemUri });
        }

        var window = (Window)Activator.CreateInstance(
            typeof(PoEToolbox.App.App).Assembly.GetType("PoEToolbox.App.MainWindow")!)!;

        var pluginContent = (ContentControl)window.FindName("PluginContent")!;
        // 探针：DesiredSize 极小的内容。越窄/越矮的内容越容易暴露「被 DesiredSize 收缩」。
        pluginContent.Content = new Border
        {
            Background = Brushes.White,
            Child = new TextBlock { Text = "x" },
        };

        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        // 内容区可用宽高都从 Body Grid 读，不重新推算，避免两处定义漂移。
        var body = FindBodyGrid(pluginContent);
        availableWidth = body.ColumnDefinitions[1].ActualWidth;
        // 高度：Body Grid 自身的可视高度就是内容区可用高度（它只有一行 *）。
        availableHeight = body.ActualHeight;

        // 页头走 FindName 而不是 Parent：ContentControl 的父级要用 (Panel) 强转，
        // 但它的逻辑父级类型不保证是 Panel。
        header = (FrameworkElement)window.FindName("ModuleHeader")!;
        return pluginContent;
    }

    /// <summary>
    /// 找 Body 那个两列 Grid（左导航定宽 / 内容区 <c>*</c>）。
    /// 从 <see cref="PluginContent"/> 往上走：它的祖先里第一个
    /// 「恰好两列且第 0 列宽度等于导航值」的 Grid 就是 Body。
    /// 不写死行号索引 —— 根 Grid 上面还有 Header / 免责声明 / 页脚，行号会漂。
    /// </summary>
    private static Grid FindBodyGrid(FrameworkElement pluginContent)
    {
        for (DependencyObject? node = pluginContent; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Grid g && g.ColumnDefinitions.Count == 2
                              && g.ColumnDefinitions[0].Width.Value == NavWidth)
                return g;
        }

        throw new InvalidOperationException(
            $"没找到 Body 的两列 Grid（左导航 {NavWidth:F0} / 内容区 *）。"
            + "如果导航列宽改了，请同步更新 ContentAreaWidthTests.NavWidth。");
    }

    private static T RunOnStaThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new System.Threading.Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        // 原样抛出去，别包成别的类型——WPF 布局异常的类型信息很值钱。
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
