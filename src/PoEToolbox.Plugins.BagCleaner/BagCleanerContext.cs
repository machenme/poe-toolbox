using PoEToolbox.Plugins.BagCleaner.Core;
using PoEToolbox.Shared;
using PoEToolbox.Plugins.BagCleaner.Services;

namespace PoEToolbox.Plugins.BagCleaner;

/// <summary>
/// Plugin-level shared context — replaces the old App.xaml.cs singletons.
/// </summary>
public static class BagCleanerContext
{
    private static CalibrationController? _calibrator;
    public static CalibrationController Calibrator =>
        _calibrator ??= new CalibrationController(Config, PoeDetector, Log);

    public static IConfigService Config { get; set; } = null!;
    public static IPoeDetector PoeDetector { get; set; } = null!;
    public static IHotkeyService Hotkey { get; set; } = null!;
    public static FileLogger Log { get; } = new();
    public static BagCleanerPlugin? Plugin { get; set; }
}
