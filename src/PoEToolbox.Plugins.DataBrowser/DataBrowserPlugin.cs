using System.Windows.Controls;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser;

public class DataBrowserPlugin : IPlugin
{
    private readonly IEventBus _eventBus;

    public DataBrowserPlugin(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _eventBus.Subscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    public string Name => "GGPK文件浏览";
    public string IconGlyph => ""; // folder
    public int Order => 15;

    private DataBrowserView? _view;

    public UserControl CreateView() => _view ??= new DataBrowserView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() => _view?.ReleaseFileLocks();
    public void OnAppShutdown()
    {
        _view?.ReleaseFileLocks();
        _eventBus.Unsubscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    private void OnReleaseGameDataLocksRequested(ReleaseGameDataLocksRequested request)
        => _view?.ReleaseFileLocks(request.KeepReopenPath);
}
