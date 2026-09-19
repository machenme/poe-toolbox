using PoEToolbox.Abstractions;

namespace PoEToolbox.Plugins.BagCleaner.Services;

/// <summary>
/// <see cref="IHotkeyServiceFactory"/> 的 BagCleaner 实现：热键服务是 WPF 消息钩子的产物，
/// 只能留在这里；消费者插件通过接口拿实例，不必引用本工程。
/// </summary>
public sealed class HotkeyServiceFactory : IHotkeyServiceFactory
{
    public IHotkeyService Create(int baseIdOffset = 0) => new HotkeyService(baseIdOffset);
}
