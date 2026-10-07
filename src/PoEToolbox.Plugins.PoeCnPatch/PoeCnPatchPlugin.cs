using System.Windows.Controls;
using PoEToolbox.Abstractions;
using PoEToolbox.Ui;

namespace PoEToolbox.Plugins.PoeCnPatch;

public sealed class PoeCnPatchPlugin : IUiPlugin
{
    private PoeCnPatchView? _view;

    public string Name => "pob国服补丁";
    public string IconGlyph => "\uE7B8"; // Segoe MDL2 Assets: Package（补丁包；与 GGPK 浏览共用 Folder 太含糊）
    public string Summary => "把 Path of Building 的交易地址与赛季切到国服，可还原。";
    public int Order => 18;

    public UserControl CreateView() => _view ??= new PoeCnPatchView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() { }
}
