using System.Windows.Controls;
using PoEToolbox.Abstractions;
using PoEToolbox.Ui;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Poe2Font;

public sealed class Poe2FontPlugin : IUiPlugin
{
    private readonly IEventBus _eventBus;
    private Poe2FontView? _view;

    public Poe2FontPlugin(IEventBus? eventBus = null)
        => _eventBus = eventBus ?? new EventBus();

    public string Name => "自定义字体";
    public string IconGlyph => "\uE8D2"; // Segoe MDL2 Assets: Font（字体，贴合「自定义字体」；与 GGPK 浏览共用 Folder 太含糊）
    public string Summary => "逐条调整游戏内 156 处字号，可整体缩放或单条微调。";
    public int Order => 11;

    public UserControl CreateView() => _view ??= new Poe2FontView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.Dispose();
}
