using System.Windows;
using Microsoft.Win32;
using PoEToolbox.Shared;

namespace PoEToolbox.Ui;

/// <summary>
/// 主题（浅色 / 深色）应用与跟随系统。
///
/// <para>
/// 切换手法：整体替换 <see cref="Application.Resources"/> 的
/// <c>MergedDictionaries[0]</c>（颜色令牌层），<b>不碰</b>后面的样式字典。原因是视图里
/// 有 300+ 处 <c>{StaticResource &lt;Style&gt;}</c>——样式在 XAML 解析期就绑定了具体对象，
/// 换样式字典会让已加载控件继续持有旧实例（外观不跟随、甚至解析失败）；换颜色字典则所有
/// <c>DynamicResource</c> 引用会自动重新求值。
/// </para>
///
/// <para>
/// WPF 没有 WinUI 的 <c>ThemeResource</c>，也没有系统主题变更广播，只能自己读注册表
/// <c>AppsUseLightTheme</c> 并订阅 <see cref="SystemParameters.StaticPropertyChanged"/>。
/// </para>
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string LightUri =
        "pack://application:,,,/PoEToolbox.Ui;component/Themes/Tokens.xaml";
    private const string DarkUri =
        "pack://application:,,,/PoEToolbox.Ui;component/Themes/Tokens.Dark.xaml";

    private static bool _subscribed;
    private static bool _followSystem = true;
    private static bool _isDark;

    /// <summary>用户是否选了「跟随系统」。存进 config.json 的 shell 段。</summary>
    public static bool FollowSystem
    {
        get => _followSystem;
        set
        {
            if (_followSystem == value) return;
            _followSystem = value;
            SavePreference();
            Apply(FollowSystem ? SystemIsDark() : _isDark);
        }
    }

    /// <summary>当前实际生效的是否为深色。</summary>
    public static bool IsDark => _isDark;

    /// <summary>
    /// 启动时调用：读用户偏好 → 落色 → 开始跟随系统。
    /// 必须在任何窗口创建前调用，晚了窗口会先按浅色画一遍再跳色。
    /// </summary>
    public static void Initialize()
    {
        var shell = ConfigService.GetPluginConfig<ShellThemeConfig>("shell");
        _followSystem = shell?.FollowSystem ?? true;
        _isDark = _followSystem ? SystemIsDark() : shell?.IsDark ?? false;

        Apply(_isDark);
        Subscribe();
    }

    /// <summary>显式切到浅色 / 深色（同时关掉「跟随系统」）。</summary>
    public static void SetDark(bool dark)
    {
        _followSystem = false;
        _isDark = dark;
        SavePreference();
        Apply(dark);
    }

    /// <summary>交还给系统主题。</summary>
    public static void UseSystem() => FollowSystem = true;

    private static void Apply(bool dark)
    {
        var resources = Application.Current?.Resources;
        if (resources?.MergedDictionaries is not { Count: > 0 } merged)
            return;

        var uri = new Uri(dark ? DarkUri : LightUri, UriKind.Absolute);

        // 已经是目标主题就不动字典：重复替换会让所有 DynamicResource 重新求值，白闪一次。
        if (merged[0].Source == uri)
            return;

        // 先装新的再摘旧的：反过来的话，中间那帧控件会拿不到任何令牌，退化成系统默认色。
        var replacement = new ResourceDictionary { Source = uri };
        var previous = merged[0];
        merged[0] = replacement;
        merged.Remove(previous);

        _isDark = dark;
    }

    private static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        // 主题切换只走SystemEvents 这一条通道。
        // 曾考虑过 SystemParameters.StaticPropertyChanged，但它对任何系统参数变动都触发
        // （鼠标悬停时长、窗口色等），与主题无关，白烧一轮全树资源重求值。
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Unsubscribe();
    }

    private static void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Category.General 覆盖「改颜色方案」这类系统外观变化；其它类别（字号、时区）与主题无关。
        if (e.Category == UserPreferenceCategory.General && FollowSystem)
            Apply(SystemIsDark());
    }

    /// <summary>
    /// 读 <c>AppsUseLightTheme</c>：0 为深色，缺失（Win10 早期）按浅色。
    /// 高对比度下强制浅色令牌 —— 深色令牌是为普通深色模式调的，高对比度要交给系统配色。
    /// </summary>
    private static bool SystemIsDark()
    {
        if (SystemParameters.HighContrast)
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn($"无法读取系统主题，按浅色继续：{ex.Message}");
            return false;
        }
    }

    private static void SavePreference()
        => ConfigService.SavePluginConfig(
            "shell",
            new ShellThemeConfig { FollowSystem = _followSystem, IsDark = _isDark });

    /// <summary>config.json 的 shell 段。命名与 <c>Shell*</c> 现有键保持一致。</summary>
    public sealed class ShellThemeConfig
    {
        public bool FollowSystem { get; set; } = true;
        public bool IsDark { get; set; }
    }
}
