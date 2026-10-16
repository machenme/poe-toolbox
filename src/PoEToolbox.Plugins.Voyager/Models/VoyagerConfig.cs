using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Voyager.Models;

public partial class VoyagerConfig
{
    // ── 复制模式 (Ctrl+C) ──────────────────────────────
    public int HotkeyTrigger { get; set; } = 0x76;             // F7
    public uint HotkeyTriggerModifier { get; set; } = 0x0002;  // MOD_CONTROL → Ctrl+F7
    public int HotkeyCalibrate { get; set; } = 0x75;           // F6
    public int Columns { get; set; } = 6;
    public int Rows { get; set; } = 10;
    public double StartX { get; set; } = 0.35;
    public double StartY { get; set; } = 0.15;
    public double StepX { get; set; } = 0.05;
    public double StepY { get; set; } = 0.05;
    public string OutputFile { get; set; } = "work/voyager_output.txt";

    // ── 智能点击模式 (截屏判空 + Ctrl+Click) ──────────
    public int SmartTriggerKey { get; set; } = 0x77;            // F8
    public uint SmartTriggerModifier { get; set; } = 0;
    public int SmartCalibrateKey { get; set; } = 0x75;          // legacy field; fixed layouts do not use it
    public int SmartColumns { get; set; } = 12;
    public int SmartRows { get; set; } = 20;
    public double SmartStartX { get; set; }
    public double SmartStartY { get; set; }
    public double SmartStepX { get; set; }
    public double SmartStepY { get; set; }

    /// <summary>像素级微调偏移 (正值=右/下，负值=左/上)。</summary>
    public int SmartOffsetX { get; set; } = 0;
    public int SmartOffsetY { get; set; } = 0;

    public bool IsSmartCalibrated => SmartStartX != 0 || SmartStartY != 0; // legacy field

    /// <summary>
    /// 源生成上下文：反射序列化在 Native AOT 下被禁用。
    /// 用 <see cref="ConfigService.CreatePluginConfigJsonOptions"/> 构造实例而非 <c>Default</c>，
    /// 保住既有 config.json 的读法（大小写不敏感 + 宽松转义）。
    /// </summary>
    [JsonSerializable(typeof(VoyagerConfig))]
    internal sealed partial class VoyagerConfigJsonContext : JsonSerializerContext;

    internal static readonly JsonTypeInfo<VoyagerConfig> ConfigTypeInfo =
        new VoyagerConfigJsonContext(ConfigService.CreatePluginConfigJsonOptions()).VoyagerConfig;
}
