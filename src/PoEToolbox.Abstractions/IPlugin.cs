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
    int Order { get; }
    void OnActivated();
    void OnDeactivated();
    void OnAppShutdown();
}
