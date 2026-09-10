using PoEToolbox.Sdk;
using System.Windows.Controls;

namespace PoEToolbox.Sdk;

/// <summary>
/// Plugin contract for the PoE Toolbox shell.
/// Each plugin implements this interface and registers at startup.
/// </summary>
public interface IPlugin
{
    string Name { get; }
    string IconGlyph { get; }
    int Order { get; }
    UserControl CreateView();
    void OnActivated();
    void OnDeactivated();
    void OnAppShutdown();
}
