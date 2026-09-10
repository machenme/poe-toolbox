using System.Drawing;

namespace PoEToolbox.Plugins.BagCleaner.Core;

/// <summary>
/// 单次清包的结果 DTO。仅用于日志/通知，不参与控制流。
/// </summary>
public sealed class CleanResult
{
    /// <summary>总格子数 (Columns * Rows)。</summary>
    public int TotalCells { get; init; }

    /// <summary>实际点击的格子数。</summary>
    public int ClickedCells { get; set; }

    /// <summary>是否被中止 (用户按 Esc 或异常)。</summary>
    public bool Aborted { get; set; }

    /// <summary>每格之间的实际间隔 (ms)，用于反检测参数调优审计。</summary>
    public List<int> IntervalsMs { get; } = new();

    /// <summary>每次点击的实际屏幕坐标，用于反检测参数调优审计。</summary>
    public List<Point> Positions { get; } = new();

    /// <summary>整个清包流程的总耗时 (ms)。</summary>
    public long ElapsedMs { get; set; }
}
