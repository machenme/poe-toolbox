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
    public string IconGlyph => ""; // edit
    public int Order => 16;

    public UserControl CreateView() => _view ??= new MapNumberView();
    public void OnActivated() { }
    public void OnDeactivated() => _view?.ReleaseFileLocks();
    public void OnAppShutdown()
    {
        _view?.ReleaseFileLocks();
        _eventBus.Unsubscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    private void OnReleaseGameDataLocksRequested(ReleaseGameDataLocksRequested _)
        => _view?.ReleaseFileLocks();
}
