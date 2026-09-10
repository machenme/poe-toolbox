namespace PoEToolbox.Plugins.BagCleaner.Models;

/// <summary>
/// 反检测参数：决定单次清包的操作模式分布。
/// PRD 明确这是 P0 安全项，调整时务必谨慎。
/// </summary>
public sealed class AntiDetectionConfig
{
    /// <summary>点击间隔下限 (ms)，默认 40</summary>
    public int ClickIntervalMinMs { get; set; } = 40;

    /// <summary>点击间隔上限 (ms)，默认 60</summary>
    public int ClickIntervalMaxMs { get; set; } = 60;

    /// <summary>坐标偏移最小值 (px)，默认 -5</summary>
    public int PositionOffsetMinPx { get; set; } = -3;

    /// <summary>坐标偏移最大值 (px)，默认 +3</summary>
    public int PositionOffsetMaxPx { get; set; } = 3;
}
