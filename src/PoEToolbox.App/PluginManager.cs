using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.App;

/// <summary>
/// Discovers and manages IPlugin instances.
/// Plugins are compiled-in at build time (no dynamic loading).
/// </summary>
public class PluginManager
{
    public IEventBus EventBus { get; }
    public IAppState SessionState => _sessionState;
    private readonly List<IPlugin> _plugins = [];
    private readonly GameSessionState _sessionState;
    public IReadOnlyList<IPlugin> Plugins => _plugins;

    public PluginManager(IEventBus? eventBus = null)
    {
        EventBus = eventBus ?? new EventBus();
        _sessionState = new GameSessionState(EventBus);
    }

    /// <summary>Register all built-in plugins.</summary>
    public void RegisterAll()
    {
        Register(new PoEToolbox.Plugins.PriceTagger.PriceTaggerPlugin(EventBus));
        Register(new PoEToolbox.Plugins.DataBrowser.DataBrowserPlugin(EventBus));
        Register(new PoEToolbox.Plugins.DataBrowser.MapNumberPlugin(EventBus));
        Register(new PoEToolbox.Plugins.BagCleaner.BagCleanerPlugin());
        Register(new PoEToolbox.Plugins.TermTranslator.TermTranslatorPlugin());
        Register(new PoEToolbox.Plugins.PoeCnPatch.PoeCnPatchPlugin());
        Register(new PoEToolbox.Plugins.Poe2Font.Poe2FontPlugin(EventBus));
        Register(new PoEToolbox.Plugins.FxPatch.FxPatchPlugin(EventBus));
        // Voyager is temporarily hidden from the navigation without removing the plugin code.
    }

    public void Register(IPlugin plugin)
    {
        _plugins.Add(plugin);
        _plugins.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public void Shutdown()
    {
        foreach (var p in _plugins)
        {
            try
            {
                p.OnAppShutdown();
            }
            catch (Exception ex)
            {
                FileLogger.WriteCritical($"Plugin shutdown failed: {p.Name}", ex);
            }
        }
        _sessionState.Dispose();
    }
}
