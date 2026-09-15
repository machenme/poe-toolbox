using System.Windows.Controls;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser;

public sealed class MapNumberPlugin : IPlugin
{
    private readonly IEventBus _eventBus;
    private MapNumberView? _view;

    public MapNumberPlugin(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _eventBus.Subscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    public string Name => "修改地图标签";
    public string IconGlyph => "\uE70F"; // Segoe MDL2 Assets: Edit（铅笔；原 E125 是旧版 Segoe UI Symbol 码点，MDL2 里不是编辑）
    public int Order => 16;

    public UserControl CreateView() => _view ??= new MapNumberView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() => _view?.ReleaseFileLocks();
    public void OnAppShutdown()
    {
        _view?.ReleaseFileLocks();
        _view?.Dispose();
        _eventBus.Unsubscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    private void OnReleaseGameDataLocksRequested(ReleaseGameDataLocksRequested _)
        => _view?.ReleaseFileLocks();
}
