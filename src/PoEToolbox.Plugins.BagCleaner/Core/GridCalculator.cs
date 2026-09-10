using System.Drawing;
using PoEToolbox.Plugins.BagCleaner.Input;
using PoEToolbox.Plugins.BagCleaner.Models;
using static PoEToolbox.Plugins.BagCleaner.Input.NativeMethods;

namespace PoEToolbox.Plugins.BagCleaner.Core;

/// <summary>
/// 网格坐标转换：相对比例 (GridConfig) ↔ 屏幕绝对坐标 (List&lt;Point&gt;)。
///
/// V2 方案核心逻辑：
/// 1. 校准时把 GetCursorPos 返回的屏幕坐标通过 ScreenToClient 转成 POE 客户区坐标，
///    再除以 GetClientRect 宽高，得到 (0.0~1.0) 比例 → 写入 GridConfig。
/// 2. 执行时反过来：GetClientRect 取当前 POE 客户区尺寸 → 比例 × 宽/高 → ClientToScreen
///    还原为屏幕绝对坐标 → 传给 InputSimulator。
/// </summary>
public static class GridCalculator
{
    /// <summary>
    /// 校准辅助：把当前光标位置 (屏幕坐标) 转成 POE 客户区比例坐标。
    /// </summary>
    /// <param name="poeHwnd">POE 窗口句柄 (前台窗口)。</param>
    /// <returns>成功返回 (relativeX, relativeY)，失败返回 null。</returns>
    public static (double relX, double relY)? CaptureRelativePoint(IntPtr poeHwnd)
    {
        if (poeHwnd == IntPtr.Zero) return null;
        if (!GetCursorPos(out var screen)) return null;
        if (!GetClientRect(poeHwnd, out var rc)) return null;

        int cw = rc.right - rc.left;
        int ch = rc.bottom - rc.top;
        if (cw <= 0 || ch <= 0) return null;

        var pt = screen;
        if (!ScreenToClient(poeHwnd, ref pt)) return null;

        double relX = (double)pt.x / cw;
        double relY = (double)pt.y / ch;
        System.Diagnostics.Debug.WriteLine($"[GridCalc.Capture] screen=({screen.x},{screen.y}) clientRect={cw}x{ch} client=({pt.x},{pt.y}) rel=({relX:F4},{relY:F4})");
        return (relX, relY);
    }

    /// <summary>
    /// 校准辅助：把 POE 客户区当前尺寸写入 GridConfig (用于运行时偏差检测)。
    /// </summary>
    public static bool TryCaptureWindowSize(IntPtr poeHwnd, out int width, out int height)
    {
        width = 0; height = 0;
        if (poeHwnd == IntPtr.Zero) return false;
        if (!GetClientRect(poeHwnd, out var rc)) return false;
        width = rc.right - rc.left;
        height = rc.bottom - rc.top;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 执行辅助：把 GridConfig (相对比例) 转为 POE 客户区坐标并映射回屏幕坐标。
    /// </summary>
    /// <param name="grid">相对比例配置。</param>
    /// <param name="poeHwnd">当前 POE 窗口句柄 (运行时由 PoeDetector 拿)。</param>
    /// <param name="actualSize">[out] 实际 POE 客户区尺寸 (用于日志)。</param>
    /// <returns>60 个屏幕绝对坐标，按行优先 (row 0 col 0, row 0 col 1, ...)。</returns>
    public static List<Point> ToAbsoluteScreenPositions(GridConfig grid, IntPtr poeHwnd, out Size actualSize)
    {
        actualSize = Size.Empty;
        var positions = new List<Point>(grid.Rows * grid.Columns);

        if (poeHwnd == IntPtr.Zero) return positions;
        if (!GetClientRect(poeHwnd, out var rc)) return positions;

        int cw = rc.right - rc.left;
        int ch = rc.bottom - rc.top;
        actualSize = new Size(cw, ch);
        if (cw <= 0 || ch <= 0) return positions;

        // 拿到 POE 窗口在屏幕上的原点 (左上角)，用于所有 ClientToScreen 转换的验证
        var origin = new POINT { x = 0, y = 0 };
        ClientToScreen(poeHwnd, ref origin);
        System.Diagnostics.Debug.WriteLine($"[GridCalc.ToAbs] poeClientRect={cw}x{ch} poeScreenOrigin=({origin.x},{origin.y})");

        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Columns; col++)
            {
                int clientX = (int)Math.Round((grid.StartX + col * grid.StepX) * cw);
                int clientY = (int)Math.Round((grid.StartY + row * grid.StepY) * ch);
                var pt = new POINT { x = clientX, y = clientY };
                if (ClientToScreen(poeHwnd, ref pt))
                {
                    positions.Add(new Point(pt.x, pt.y));
                    // 边界点日志 (用于诊断坐标偏差)
                    if (col == 0 && row == 0)
                        System.Diagnostics.Debug.WriteLine($"[GridCalc.ToAbs] cell[0,0] client=({clientX},{clientY}) -> screen=({pt.x},{pt.y})");
                    if (col == grid.Columns - 1 && row == grid.Rows - 1)
                        System.Diagnostics.Debug.WriteLine($"[GridCalc.ToAbs] cell[last,last] client=({clientX},{clientY}) -> screen=({pt.x},{pt.y})");
                }
            }
        }

        return positions;
    }
}
