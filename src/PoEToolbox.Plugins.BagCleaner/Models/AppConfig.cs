using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoEToolbox.Core.ScreenGrid;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.BagCleaner.Models;

/// <summary>
/// 根配置：所有可调参数的聚合根。
/// </summary>
public sealed partial class AppConfig
{
    public HotkeyConfig Hotkey { get; set; } = new();
    public GridConfig Grid { get; set; } = new();
    public TimingConfig Timing { get; set; } = new();
    public AntiDetectionConfig AntiDetection { get; set; } = new();
    public List<string> PoeProcessNames { get; set; } = ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExileEGL"];

    /// <summary>
    /// 源生成上下文：反射序列化在 Native AOT 下被禁用。注册根类型即可，嵌套的
    /// Hotkey/Grid/Timing/AntiDetection 由生成器顺着属性自动带出来。
    /// 用 <see cref="ConfigService.CreatePluginConfigJsonOptions"/> 构造实例而非 <c>Default</c>，
    /// 保住既有 config.json 的读法（大小写不敏感 + 宽松转义）。
    /// </summary>
    [JsonSerializable(typeof(AppConfig))]
    internal sealed partial class AppConfigJsonContext : JsonSerializerContext;

    internal static readonly JsonTypeInfo<AppConfig> ConfigTypeInfo =
        new AppConfigJsonContext(ConfigService.CreatePluginConfigJsonOptions()).AppConfig;
}
