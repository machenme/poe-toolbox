using System.Windows.Controls;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.PoeCnPatch;

public sealed class PoeCnPatchPlugin : IPlugin
{
    private PoeCnPatchView? _view;

    public string Name => "pob国服补丁";
    public string IconGlyph => "\uE7B8"; // Segoe MDL2 Assets: Package（补丁包；与 GGPK 浏览共用 Folder 太含糊）
    public int Order => 18;

    public UserControl CreateView() => _view ??= new PoeCnPatchView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() { }
}
