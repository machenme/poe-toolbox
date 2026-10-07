using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoEToolbox.App;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// Dashboard 卡片的<b>几何</b>守卫：图标不许压住模块名，可用卡片不许常驻角标。
///
/// <para>
/// 2026-10-07 修的两个 bug，既有测试一个都拦不住：
/// </para>
/// <list type="bullet">
/// <item>标题行图标 <c>Border</c> 只写了 <c>Width="32"</c> 没写
/// <c>HorizontalAlignment</c>。WPF 里 <c>Stretch</c> 撞上显式 <c>Width</c>
/// 会退化成 <c>Center</c>，32px 图标被居中到列中部，直接盖在模块名上。</item>
/// <item><c>UnavailableBadge</c> 样式注释写着「默认 Collapsed」，但代码里从没设过这个
/// 默认值，而 <c>Border.Visibility</c> 默认是 <c>Visible</c> ⇒ 角标对可用卡片也常驻，
/// 把标题列宽吃掉，模块名被压成「物化标注…」。</item>
/// </list>
///
/// <para>
/// 为什么必须真跑 WPF：<c>XamlTokenGuardTests</c> 那一批全是拿正则读源码文本，
/// 看得见「写没写 <c>HorizontalAlignment</c>」，却量不出两个元素实际是否重叠。
/// 这里复用 <c>XamlLoadTests</c> 的 STA 脚手架，真的 Measure/Arrange 一遍，
/// 再比对 <see cref="VisualTreeHelper.GetDescendantBounds"/> 给出的矩形。
/// </para>
/// </summary>
public sealed class DashboardCardLayoutTests
{
    /// <summary>
    /// 图标与模块名不许有交集。两者曾经同占第 0 列、靠 <c>Margin="42,0,8,0"</c>
    /// 硬凑偏移避让 —— 那个 42 是写死的，改字号或改图标尺寸立刻错位。
    /// </summary>
    [Fact]
    public void CardIconDoesNotOverlapModuleName()
    {
        var parts = MeasureFirstCard("特效补丁", enabled: true);

        Assert.True(parts.Icon.Width > 0 && parts.Icon.Height > 0,
            "图标没有布局尺寸，说明它被折叠了");
        Assert.True(parts.Title.Width > 0,
            "模块名宽度为 0，说明标题被角标挤没了");

        // 留 0.5px 容差：WPF 的 Arrange 常有亚像素舍入，卡死了会误报。
        var overlap = parts.Icon.Left < parts.Title.Right - 0.5
                      && parts.Title.Left < parts.Icon.Right - 0.5;
        Assert.False(overlap,
            $"图标 [{parts.Icon.Left:F1},{parts.Icon.Right:F1}] "
            + $"与模块名 [{parts.Title.Left:F1},{parts.Title.Right:F1}] 重叠");

        // 图标应在模块名左边，且留出可见的间距（10px）。
        // 只断言「不重叠」不够：万一以后有人把间距调到 0，两字会紧贴成「、特效补丁」，
        // 视觉上仍是坏的，但不满足 overlap 判定。
        Assert.True(parts.Title.Left - parts.Icon.Right >= 8,
            $"图标与模块名之间只剩 {parts.Title.Left - parts.Icon.Right:F1}px 间距，"
            + "两者会贴在一起");
    }

    /// <summary>
    /// 可用卡片不许显示「需另一个客户端」角标。角标在第 2 列（Auto），
    /// 常驻会把第 1 列的可用宽度吃掉，长模块名一律被截成「物化标注…」。
    /// </summary>
    [Fact]
    public void AvailableCardHidesUnavailableBadge()
    {
        var parts = MeasureFirstCard("词缀上色", enabled: true);
        Assert.Equal(Visibility.Collapsed, parts.BadgeElement.Visibility);
    }

    /// <summary>
    /// 角标必须真的能被打开 —— 上一条只验了默认收起，
    /// 万一 DataTrigger 写坏了，会变成「永远不显示」而无人发现。
    /// </summary>
    [Fact]
    public void UnavailableBadgeShowsWhenCardDisabled()
    {
        var parts = MeasureFirstCard("标签清理", enabled: false);
        Assert.Equal(Visibility.Visible, parts.BadgeElement.Visibility);
    }

    private readonly record struct CardParts(
        Rect Icon, Rect Title, Rect Badge, Border BadgeElement);

    /// <summary>
    /// 造一个只含单张卡片的 DashboardView，Measure + Arrange 到定宽，
    /// 返回那张卡片里三个关键元素的矩形（已换算到同一原点，跨元素才可比）。
    /// </summary>
    private static CardParts MeasureFirstCard(string name, bool enabled)
    {
        return RunOnStaThread(() =>
        {
            var view = BuildDashboardView(new ModuleCard(
                name,
                "",                          // 图标字形不参与断言
                "占位说明文本，长度足够触发换行但不影响标题行的几何。",
                () => enabled));

            const double width = 1180;
            view.Measure(new Size(width, 800));
            view.Arrange(new Rect(0, 0, width, 800));
            view.UpdateLayout();

            var card = FindFirstCardContainer(view)
                       ?? throw new InvalidOperationException("没找到卡片容器");

            // 定位图标底板只认「32x32 且不含角标触发器」这一个结构特征。
            // ⛔ 不能用 HorizontalAlignment==Left 之类的写法特征来定位：
            // 那等于把「修没修」写进查找条件，bug 版本会因为「找不到元素」而失败，
            // 而不是因为「检测到重叠」而失败 —— 负控制实验已经证明过这一点，
            // 那样得到的守卫是假的。
            var icon = FindByPredicate<Border>(card, e => e is Border b
                                                      && b.Width == 32 && b.Height == 32
                                                      && !HasIsEnabledTrigger(b))
                       ?? throw new InvalidOperationException("没找到 32x32 的图标底板");
            var title = FindByPredicate<TextBlock>(card, e => e is TextBlock t && t.Text == name)
                        ?? throw new InvalidOperationException("没找到模块名 TextBlock");
            var badge = FindBadge(card)
                        ?? throw new InvalidOperationException("没找到 UnavailableBadge 角标");

            // 三个矩形统一换算到卡片自身的坐标系：断言时用同一套原点。
            // 用 TransformToAncestor 而不是 GetDescendantBounds —— 后者对
            // Collapsed 元素（默认收起的角标）会返回负尺寸的 Rect，构造就抛。
            return new CardParts(
                BoundsIn(icon, card),
                BoundsIn(title, card),
                BoundsIn(badge, card),
                badge);
        });
    }

    /// <summary>取元素在 <paramref name="ancestor"/> 坐标系里的矩形。</summary>
    private static Rect BoundsIn(FrameworkElement element, FrameworkElement ancestor)
    {
        var topLeft = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
        return new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
    }

    /// <summary>按「带 IsEnabled DataTrigger 的 Border」认出角标，避免依赖 x:Name。</summary>
    private static bool HasIsEnabledTrigger(Border b)
        => b.Style?.Triggers.OfType<DataTrigger>()
            .Any(t => t.Binding is System.Windows.Data.Binding bind
                      && bind.Path.Path == "IsEnabled") == true;

    private static Border? FindBadge(DependencyObject root) => FindByPredicate<Border>(root, e =>
        e is Border b && HasIsEnabledTrigger(b));

    private static T? FindByPredicate<T>(DependencyObject root, Func<DependencyObject, bool> match)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (match(child) && child is T typed)
                return typed;
            var found = FindByPredicate<T>(child, match);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// 找卡片根 Border（<c>Tag="{Binding}"</c> 绑的是 <see cref="ModuleCard"/>）。
    /// 不要走 <c>ItemContainerGenerator.ContainerFromIndex</c>：容器是 ContentPresenter，
    /// 它的父节点是 WrapPanel 而不是卡片 Border，取父节点会拿到 null。
    /// </summary>
    private static Border? FindFirstCardContainer(DependencyObject root)
        => FindByPredicate<Border>(root, e => e is Border b && b.Tag is ModuleCard);

    /// <summary>
    /// 造一个只挂设计系统的 DashboardView。
    /// 不能直接 new MainWindow：这里只需要卡片视图本身，不必把整个外壳拉起来。
    /// </summary>
    private static DashboardView BuildDashboardView(ModuleCard card)
    {
        // pack:// 的 UriParser 必须显式注册，否则 new Uri("pack://application:,,,/...")
        // 会把 ",,," 当端口号抛 UriFormatException（与 XamlLoadTests 同一个坑）。
        if (!UriParser.IsKnownScheme("pack"))
            UriParser.Register(new GenericUriParser(GenericUriParserOptions.GenericAuthority), "pack", -1);

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

        IReadOnlyList<ModuleCard> cards = new[] { card };
        return new DashboardView(
            () => cards,
            () => PoEToolbox.Shared.PoeGameKind.Poe2,
            () => false,
            () => null);
    }

    /// <summary>
    /// WPF 控件必须在 STA 线程上创建，且整段（Application 构造、控件 new、Measure）
    /// 都得留在同一根 STA 线程 —— 只把 <c>new Application()</c> 挪到 STA、
    /// 控件留在原线程同样会炸（<c>InputManager</c> 是第一次构造控件时才建的，
    /// 要求当前线程是 STA）。
    /// </summary>
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

        // 原样抛出去，别包成别的类型——XamlParseException 的行号信息很值钱。
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
