namespace PoEToolbox.Abstractions;

/// <summary>
/// 创建 <see cref="IHotkeyService"/>。实现方（BagCleaner 的 WPF 热键服务）由组合根注入，
/// 使消费者插件无需引用 BagCleaner。
/// </summary>
public interface IHotkeyServiceFactory
{
    /// <summary>
    /// 创建一个独立的热键服务实例。
    /// baseIdOffset 用于多插件/多功能共存时避免 Win32 热键 ID 冲突。
    /// </summary>
    IHotkeyService Create(int baseIdOffset = 0);
}
