using System.Windows.Controls;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.Poe2Font;

public sealed class Poe2FontPlugin : IPlugin
{
    private Poe2FontView? _view;

    public string Name => "POE2字体配置";
    public string IconGlyph => "";
    public int Order => 10;

    public UserControl CreateView() => _view ??= new Poe2FontView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() { }
}
