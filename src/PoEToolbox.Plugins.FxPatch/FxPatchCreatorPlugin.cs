using System.Windows.Controls;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

/// <summary>「创建补丁」模块：特效补丁管理介绍 + 补丁生成器（POE2）。</summary>
public sealed class FxPatchCreatorPlugin : IPlugin
{
    private readonly IEventBus _eventBus;
    private FxPatchCreatorView? _view;

    public FxPatchCreatorPlugin(IEventBus? eventBus = null)
        => _eventBus = eventBus ?? new EventBus();

    public string Name => "创建补丁";
    public string IconGlyph => "\uE90F"; // Segoe MDL2 Assets: Repair
    public int Order => 10;

    public UserControl CreateView() => _view ??= new FxPatchCreatorView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.Dispose();
}
