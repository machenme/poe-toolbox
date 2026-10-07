using System;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 真正把 XAML 装载一遍的守卫。
///
/// <para>
/// 2026-10-07踩的坑：给内容区加居中时写了
/// <c>&lt;Grid MaxWidth="1180" HorizontalAlignment="Center" Width="100%"&gt;</c>，
/// 编译通过、263 条测试全绿、真机启动立刻崩
/// （<c>XamlParseException: 在 System.Windows.Baml2006.TypeConverterMarkupExtension
/// 上提供值时引发了异常</c>）。
/// </para>
///
/// <para>
/// <b>为什么已有测试全都拦不住</b>：项目里所有 XAML 守卫
/// （<c>XamlTokenGuardTests</c>、<c>XamlGridIndexTests</c>、<c>SingleThemeTests</c>
/// …）都是<b>拿正则读源码文本</b>，检查的是「写法对不对」。
/// 而这次的问题是「WPF 运行时能不能解析这个值」——正则看到一个合法的
/// <c>Width="100%"</c>，看不出它会在解析期炸。
/// </para>
///
/// <para>
/// 所以这类测试必须走真实的 WPF 加载路径：<see cref="Application.LoadComponent"/>。
/// 它是崩溃栈里点名的那一帧，能把 <c>XamlParseException</c> 连同
/// <c>InnerException</c> 和行号一起抛出来。
/// </para>
/// </summary>
public sealed class XamlLoadTests
{
    /// <summary>
    /// 装载资源字典链 + <c>MainWindow</c>，任何 XAML 解析失败都会在这里抛出。
    ///
    /// <para>
    /// 为什么要自己new <see cref="Application"/> 并手工挂字典：
    /// 测试进程里没有 <c>App</c> 实例（不会跑 <c>OnStartup</c>），
    /// <c>Application.Resources</c> 是空的，直接 LoadComponent 会因为
    /// <c>{StaticResource}</c> 找不到键而失败——那测的是「没挂字典」，
    /// 不是「字典和视图配不配」。所以按 <c>App.xaml</c> 里的写法原样挂一遍。
    /// </para>
    ///
    /// <para>
    /// 整段必须跑在 STA 线程上，不只是 <c>new Application()</c>：
    /// WPF 的 <c>InputManager</c> 是在第一次构造控件时才建的，它要求当前线程是 STA，
    /// 在 MTA 上会抛「调用线程必须为 STA」。只把 Application 的构造挪到 STA 而
    /// LoadComponent 留在原线程，同样会炸——踩过一次。
    /// </para>
    /// </summary>
    [Fact]
    public void ShellWindowLoadsWithDesignSystemApplied()
    {
        var window = RunOnStaThread(() =>
        {
            // pack:// 的 UriParser 必须显式注册，否则 new Uri("pack://application:,,,/...")
            // 会把 ",,," 当端口号，抛 UriFormatException: Invalid port specified。
            // WPF 应用启动时 Application 会替你注册；裸测试进程没人做这件事。
            // 重复注册会抛，所以先查再注册（第二个测试进来时走Registered 那条）。
            if (!UriParser.IsKnownScheme("pack"))
                UriParser.Register(new GenericUriParser(GenericUriParserOptions.GenericAuthority), "pack", -1);

            // 与 App.xaml 里的路径保持一致：设计系统先于任何视图装载，
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

            return (Window)Activator.CreateInstance(
                typeof(PoEToolbox.App.App).Assembly.GetType("PoEToolbox.App.MainWindow")!)!;
        });

        Assert.NotNull(window);
    }

    /// <summary>
    /// 在专用 STA 线程上跑 <paramref name="body"/>，把异常原样搬回调用线程。
    ///
    /// <para>
    /// <see cref="Application"/> 全进程只能有一个（第二个 <c>new</c> 会抛），
    /// 而 xunit 同一进程内会跑多个测试，所以复用 <see cref="Application.Current"/>。
    /// 注意 STA 线程退出后 <see cref="Application.Current"/> 仍在，
    /// 下一个测试再进来时不会重复创建。
    /// </para>
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
