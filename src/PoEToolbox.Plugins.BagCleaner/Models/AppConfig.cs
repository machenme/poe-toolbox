namespace PoEToolbox.Plugins.BagCleaner.Models;

/// <summary>
/// 根配置：所有可调参数的聚合根。
/// </summary>
public sealed class AppConfig
{
    public HotkeyConfig Hotkey { get; set; } = new();
    public GridConfig Grid { get; set; } = new();
    public TimingConfig Timing { get; set; } = new();
    public AntiDetectionConfig AntiDetection { get; set; } = new();
    public List<string> PoeProcessNames { get; set; } = ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExileEGL"];
}
