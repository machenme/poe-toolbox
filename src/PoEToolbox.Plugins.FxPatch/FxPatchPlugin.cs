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
    public string IconGlyph => "";
    public int Order => 9;

    public UserControl CreateView() => _view ??= new FxPatchView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.Dispose();
}
