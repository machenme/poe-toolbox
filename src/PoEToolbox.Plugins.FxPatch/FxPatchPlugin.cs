using System.Windows.Controls;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

public sealed class FxPatchPlugin : IPlugin
{
    private readonly IEventBus _eventBus;
    private FxPatchView? _view;

    public FxPatchPlugin(IEventBus? eventBus = null)
        => _eventBus = eventBus ?? new EventBus();

    public string Name => "特效补丁";
    public string IconGlyph => "\uE945"; // Segoe MDL2 Assets: LightningBolt
    public int Order => 9;

    public UserControl CreateView() => _view ??= new FxPatchView(_eventBus);

    /// <summary>切到本模块时按补丁记录刷新「哪些补丁已经打上」并默认勾选——只读一个小 json，不打开索引。</summary>
    public void OnActivated() => _view?.RefreshStatusIfStale();
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.Dispose();
}
