using PoEToolbox.Sdk;
using System.Windows.Controls;
using System.Windows.Threading;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.PriceTagger;

public class PriceTaggerPlugin : IPlugin
{
    /// <summary>
    /// How long the module stays alive after the user switches to another one.
    /// Short enough to actually give the memory back, long enough that clicking
    /// the wrong tab does not throw the view away.
    /// </summary>
    private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromSeconds(5);

    private readonly IEventBus _eventBus;
    private readonly DispatcherTimer _idleReleaseTimer;
    private PriceTaggerView? _view;

    public PriceTaggerPlugin(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _idleReleaseTimer = new DispatcherTimer { Interval = IdleReleaseDelay };
        _idleReleaseTimer.Tick += (_, _) => ReleaseIfIdle();
    }

    public string Name => UILabels.Get("PluginPriceTagger");
    public string IconGlyph => ""; // chart glyph
    public int Order => 0;

    public UserControl CreateView() => _view ??= new PriceTaggerView(_eventBus);

    public void OnActivated() => _idleReleaseTimer.Stop();

    public void OnDeactivated()
    {
        // Restart the countdown — a quick round trip keeps the view alive.
        _idleReleaseTimer.Stop();
        _idleReleaseTimer.Start();
    }

    public void OnAppShutdown()
    {
        _idleReleaseTimer.Stop();
        ReleaseView();
    }

    /// <summary>
    /// Fires 5s after leaving the module. If a detect/apply job is still running the
    /// timer stays armed and tries again on the next tick, instead of pulling the
    /// view out from under the running task.
    /// </summary>
    private void ReleaseIfIdle()
    {
        if (_view is { IsBusy: true })
            return;

        _idleReleaseTimer.Stop();
        ReleaseView();
    }

    private void ReleaseView()
    {
        var view = _view;
        if (view is null) return;

        _view = null;                  // drop the plugin's reference first ...
        view.Dispose();
        view.ReleaseMemory();          // ... then let the view drop its own data
        MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
    }
}
