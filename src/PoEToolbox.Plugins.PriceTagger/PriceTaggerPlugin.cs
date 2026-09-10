using PoEToolbox.Sdk;
using System.Windows.Controls;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.PriceTagger;

public class PriceTaggerPlugin : IPlugin
{
    private readonly IEventBus _eventBus;

    public PriceTaggerPlugin(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
    }

    public string Name => UILabels.Get("PluginPriceTagger");
    public string IconGlyph => ""; // chart glyph
    public int Order => 0;

    public UserControl CreateView() => new PriceTaggerView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() { }
}
