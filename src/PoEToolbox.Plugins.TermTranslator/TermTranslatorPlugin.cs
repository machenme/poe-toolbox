using System.Windows.Controls;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.TermTranslator;

public sealed class TermTranslatorPlugin : IPlugin
{
    private TermTranslatorView? _view;

    public string Name => "攻略翻译";
    public string IconGlyph => "\uE82D"; // Segoe MDL2 Assets: Dictionary（词典，贴合「攻略翻译」）
    public int Order => 12;

    public UserControl CreateView() => _view ??= new TermTranslatorView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.CancelTranslation();
}
