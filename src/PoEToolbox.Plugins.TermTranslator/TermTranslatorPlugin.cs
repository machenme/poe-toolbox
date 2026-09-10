using System.Windows.Controls;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.TermTranslator;

public sealed class TermTranslatorPlugin : IPlugin
{
    private TermTranslatorView? _view;

    public string Name => "术语翻译";
    public string IconGlyph => "";
    public int Order => 12;

    public UserControl CreateView() => _view ??= new TermTranslatorView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnAppShutdown() => _view?.CancelTranslation();
}
