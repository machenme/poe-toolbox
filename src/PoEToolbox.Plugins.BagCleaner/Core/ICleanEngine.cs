using System.Drawing;
using PoEToolbox.Plugins.BagCleaner.Models;

namespace PoEToolbox.Plugins.BagCleaner.Core;

/// <summary>
/// 清包引擎接口 (符合 SPEC §4.2.2)。
/// </summary>
public interface ICleanEngine
{
    /// <summary>引擎当前状态。</summary>
    CleanState State { get; }

    /// <summary>
    /// 异步执行一次完整清包周期 (V2 方案 — 接收已转换好的绝对屏幕坐标)。
    /// </summary>
    /// <param name="positions">60 个屏幕绝对坐标 (已由 GridCalculator 基于 POE ClientArea 算好)。</param>
    /// <param name="antiDet">反检测参数。</param>
    /// <param name="moveSettleDelayMs">移动后到点击前的等待 (来自 TimingConfig)。</param>
    /// <param name="ct">外部中断令牌 (来自停止热键)。</param>
    Task<CleanResult> ExecuteAsync(
        IReadOnlyList<Point> positions,
        AntiDetectionConfig antiDet,
        int moveSettleDelayMs,
        CancellationToken ct);
}
