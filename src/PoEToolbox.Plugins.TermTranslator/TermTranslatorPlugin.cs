using System.Windows.Controls;
using PoEToolbox.Abstractions;
using PoEToolbox.Ui;

namespace PoEToolbox.Plugins.TermTranslator;

public sealed class TermTranslatorPlugin : IUiPlugin
{
    private TermTranslatorView? _view;

    public string Name => "攻略翻译";
    public string IconGlyph => "\uE82D"; // Segoe MDL2 Assets: Dictionary（词典，贴合「攻略翻译」）
    public string Summary => "用内置术语库翻译攻略文本，后台执行、可取消。";
    public int Order => 12;

    public UserControl CreateView() => _view ??= new TermTranslatorView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.CancelTranslation();
}
