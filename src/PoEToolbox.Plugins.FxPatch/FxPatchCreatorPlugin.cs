using System.Windows.Controls;
using PoEToolbox.Abstractions;
using PoEToolbox.Ui;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

/// <summary>「创建补丁」模块：特效补丁管理介绍 + 补丁生成器（POE2）。</summary>
public sealed class FxPatchCreatorPlugin : IUiPlugin
{
    private readonly IEventBus _eventBus;
    private FxPatchCreatorView? _view;

    public FxPatchCreatorPlugin(IEventBus? eventBus = null)
        => _eventBus = eventBus ?? new EventBus();

    public string Name => "创建补丁";
    public string IconGlyph => "\uE90F"; // Segoe MDL2 Assets: Repair
    public string Summary => "对比原版与改后两份游戏数据，生成可分发的补丁包。";
    public int Order => 10;

    public UserControl CreateView() => _view ??= new FxPatchCreatorView(_eventBus);
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.Dispose();
}
