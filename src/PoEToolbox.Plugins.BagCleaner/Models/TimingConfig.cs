namespace PoEToolbox.Plugins.BagCleaner.Models;

/// <summary>
/// 时间相关配置：仅保留与点击节奏直接相关的最小集合。
/// 反检测随机区间在 <see cref="AntiDetectionConfig"/>。
/// </summary>
public sealed class TimingConfig
{
    /// <summary>鼠标移动后到点击前的等待时间 (ms)，给游戏光标反应时间</summary>
    public int MoveSettleDelayMs { get; set; } = 10;
}
