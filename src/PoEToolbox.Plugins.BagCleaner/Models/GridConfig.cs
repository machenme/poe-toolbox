namespace PoEToolbox.Plugins.BagCleaner.Models;

/// <summary>
/// 背包网格坐标配置 (POE ClientArea 相对坐标方案 — 参考 §7.2 V2)。
///
/// 关键变更：
/// 1. StartX/Y/StepX/Y 全部改为 double (0.0~1.0 比例) — 相对于 POE 窗口客户区。
/// 2. 增加 <see cref="PoeWindowWidth"/> / <see cref="PoeWindowHeight"/> 记录校准时
///    POE 窗口客户区尺寸。运行时若窗口尺寸变化，会在日志中提示偏差风险。
///
/// 优势 (相对原绝对坐标方案)：
/// - 跨显示器 / DPI 缩放 / 窗口大小变化 都能保持正确
/// - 切换窗口化全屏 / 居中 / 不同分辨率不需要重新校准
/// - 多 POE 客户端 (Steam / EGL / 官方) 用同一配置即可
/// </summary>
public sealed class GridConfig
{
    /// <summary>背包第 1 格中心 X — 相对 POE 客户区宽度的比例 (0.0~1.0)。</summary>
    public double StartX { get; set; } = 0.505;

    /// <summary>背包第 1 格中心 Y — 相对 POE 客户区高度的比例 (0.0~1.0)。</summary>
    public double StartY { get; set; } = 0.426;

    /// <summary>相邻格子 X 方向间距 — 相对 POE 客户区宽度 (0.0~1.0)。</summary>
    public double StepX { get; set; } = 0.0207;

    /// <summary>相邻格子 Y 方向间距 — 相对 POE 客户区高度 (0.0~1.0)。</summary>
    public double StepY { get; set; } = 0.0368;

    /// <summary>列数 (POE 背包默认为 12)。</summary>
    public int Columns { get; set; } = 12;

    /// <summary>行数 (POE 背包默认为 5)。</summary>
    public int Rows { get; set; } = 5;

    /// <summary>校准时 POE 客户区宽度 (px, 物理像素)。运行时仅用于偏差提示。</summary>
    public int PoeWindowWidth { get; set; } = 1920;

    /// <summary>校准时 POE 客户区高度 (px, 物理像素)。运行时仅用于偏差提示。</summary>
    public int PoeWindowHeight { get; set; } = 1080;

    /// <summary>
    /// 跳过标记数组 (true=跳过该格, false=正常清理)。
    /// 长度应为 Columns×Rows (默认 60)。null 或空 = 全不跳过。
    /// 索引顺序：行优先 (row 0 col 0, row 0 col 1, ..., row N col M)。
    /// </summary>
    public bool[]? SkipMask { get; set; }
}
