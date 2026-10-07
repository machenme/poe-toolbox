using System.Windows.Controls;
using PoEToolbox.Abstractions;
using PoEToolbox.Ui;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser;

public class DataBrowserPlugin : IUiPlugin
{
    private readonly IEventBus _eventBus;

    public DataBrowserPlugin(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _eventBus.Subscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    public string Name => "GGPK文件浏览";
    public string IconGlyph => "\uE8B7"; // folder
    public string Summary => "浏览、查找、批量替换游戏数据文件里的文本，改前可备份。";
    public int Order => 15;

    private DataBrowserView? _view;

    public UserControl CreateView() => _view ??= new DataBrowserView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() => _view?.ReleaseFileLocks();
    public bool TryPrepareForAppClose() => _view?.ReleaseFileLocks() ?? true;
    public void OnAppShutdown()
    {
        _view?.ReleaseFileLocks();
        _eventBus.Unsubscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    private void OnReleaseGameDataLocksRequested(ReleaseGameDataLocksRequested request)
        => _view?.ReleaseFileLocks(request.KeepReopenPath);
}
