using System.Windows.Controls;
using PoEToolbox.Abstractions;
using PoEToolbox.Ui;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench;

public class AffixWorkbenchPlugin : IUiPlugin
{
    private readonly IEventBus _eventBus;

    public AffixWorkbenchPlugin(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _eventBus.Subscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    public string Name => "词缀上色";
    public string IconGlyph => "\uE790"; // Segoe MDL2 Assets: Color（调色盘，贴合「词缀上色」；原 E7EC 实为 DrivingMode）
    public string Summary => "给全部词缀按稀有度上色，改动写成游戏补丁，随时可一键还原。";
    public int Order => 17;

    private AffixWorkbenchView? _view;

    public UserControl CreateView() => _view ??= new AffixWorkbenchView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() => _view?.ReleaseFileLocks();
    public bool TryPrepareForAppClose() => _view?.ReleaseFileLocks() ?? true;
    public void OnAppShutdown()
    {
        _view?.ReleaseFileLocks();
        _eventBus.Unsubscribe<ReleaseGameDataLocksRequested>(OnReleaseGameDataLocksRequested);
    }

    private void OnReleaseGameDataLocksRequested(ReleaseGameDataLocksRequested request)
        => _view?.ReleaseFileLocks();
}
