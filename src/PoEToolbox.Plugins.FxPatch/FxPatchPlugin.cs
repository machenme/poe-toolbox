using System.Windows.Controls;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.FxPatch;

public sealed class FxPatchPlugin : IPlugin
{
    private FxPatchView? _view;

    public string Name => "特效补丁";
    public string IconGlyph => "";
    public int Order => 9;

    public UserControl CreateView() => _view ??= new FxPatchView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() { }
}
