using System.Text.Json;
using PoEToolbox.Plugins.BagCleaner.Models;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.BagCleaner.Services;

/// <summary>
/// 配置服务：读写 %LOCALAPPDATA%\PoEToolbox\config.json 中的 BagCleaner 段。
/// </summary>
public interface IConfigService
{
    AppConfig Current { get; }
    AppConfig LoadConfig();
    void SaveConfig(AppConfig config);
}

public sealed class ConfigService : IConfigService
{
    private const string PluginKey = "BagCleaner";
    private AppConfig _current = new();

    public AppConfig Current => _current;

    public AppConfig LoadConfig()
    {
        try
        {
            var loaded = Shared.ConfigService.GetPluginConfig<AppConfig>(PluginKey);
            if (loaded == null)
            {
                _current = new AppConfig();
                SaveConfig(_current);
                return _current;
            }

            _current = loaded;

            // V1 → V2 migration
            if (IsLegacyAbsoluteCoordinateFormat(_current))
            {
                _current.Grid = new GridConfig();
            }

            Normalize(_current);
            return _current;
        }
        catch
        {
            _current = new AppConfig();
            return _current;
        }
    }

    public void SaveConfig(AppConfig config)
    {
        Normalize(config);
        Shared.ConfigService.SavePluginConfig(PluginKey, config);
        _current = config;
    }

    // ═══ helpers ═══════════════════════════════════════

    private static bool IsLegacyAbsoluteCoordinateFormat(AppConfig cfg)
        => cfg.Grid.StartX > 1.0 || cfg.Grid.StepX > 0.1
        || cfg.Grid.StartY > 1.0 || cfg.Grid.StepY > 0.1;

    private static void Normalize(AppConfig cfg)
    {
        cfg.Hotkey ??= new HotkeyConfig();
        if (cfg.Hotkey.TriggerKey == 0) cfg.Hotkey.TriggerKey = 0x71;
        if (cfg.Hotkey.StopKey == 0) cfg.Hotkey.StopKey = 0x1B;
        if (cfg.Hotkey.CalibrateKey == 0) cfg.Hotkey.CalibrateKey = 0x72;
        cfg.Grid ??= new GridConfig();
        if (cfg.Grid.Columns < 1) cfg.Grid.Columns = 12;
        if (cfg.Grid.Rows < 1) cfg.Grid.Rows = 5;
        cfg.Timing ??= new TimingConfig();
        cfg.AntiDetection ??= new AntiDetectionConfig();
        cfg.PoeProcessNames ??= ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExileEGL"];
        if (cfg.PoeProcessNames.Count == 0)
            cfg.PoeProcessNames = ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExileEGL"];
    }
}
