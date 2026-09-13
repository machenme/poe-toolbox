using System.Windows.Controls;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Poe2Font;

public sealed class Poe2FontPlugin : IPlugin
{
    private readonly IEventBus _eventBus;
    private Poe2FontView? _view;

    public Poe2FontPlugin(IEventBus? eventBus = null)
        => _eventBus = eventBus ?? new EventBus();

    public string Name => "自定义字体";
    public string IconGlyph => "";
    public int Order => 10;

    public UserControl CreateView() => _view ??= new Poe2FontView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.Dispose();
}
