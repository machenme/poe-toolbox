using System.Windows.Controls;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.PoeCnPatch;

public sealed class PoeCnPatchPlugin : IPlugin
{
    private PoeCnPatchView? _view;

    public string Name => "pob国服补丁";
    public string IconGlyph => "";
    public int Order => 18;

    public UserControl CreateView() => _view ??= new PoeCnPatchView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() { }
}
