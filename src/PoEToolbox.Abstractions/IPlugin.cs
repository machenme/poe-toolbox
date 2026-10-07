namespace PoEToolbox.Abstractions;

/// <summary>
/// Plugin contract for the PoE Toolbox shell.
/// Each plugin implements this interface and registers at startup.
/// 视图创建不在这个契约里：它属于 UI 层（<c>PoEToolbox.Ui.IUiPlugin</c>），
/// 这样本程序集不需要引用 WPF。
/// </summary>
public interface IPlugin
{
    string Name { get; }
    string IconGlyph { get; }

    /// <summary>
    /// 一句话说明这个模块会动什么（中文，与 <see cref="Name"/> 同语言）。
    /// Dashboard 首页的卡片靠它告诉用户点进去会发生什么，所以要写「会改什么」而不是「是什么」。
    /// 无界面插件返回空串即可。
    /// </summary>
    string Summary { get; }

    int Order { get; }
    void OnActivated();
    void OnDeactivated();
    void OnAppShutdown();
}
