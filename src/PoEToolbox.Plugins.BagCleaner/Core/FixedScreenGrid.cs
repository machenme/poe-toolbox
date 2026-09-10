using System.Drawing;
using System.Runtime.InteropServices;
using static PoEToolbox.Plugins.BagCleaner.Input.NativeMethods;

namespace PoEToolbox.Plugins.BagCleaner.Core;

/// <summary>
/// 固定屏幕网格的通用坐标工具。
/// 外框坐标相对于目标窗口所在显示器左上角，输出为虚拟桌面绝对坐标。
/// </summary>
public static class FixedScreenGrid
{
    public static bool TryGetScreenRect(
        IntPtr windowHandle,
        int monitorWidth,
        int monitorHeight,
        int left,
        int top,
        int right,
        int bottom,
        out Rectangle rect,
        out string error)
    {
        rect = Rectangle.Empty;
        error = string.Empty;

        if (windowHandle == IntPtr.Zero)
        {
            error = "未找到目标窗口";
            return false;
        }

        var monitor = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            error = "无法获取目标窗口所在显示器";
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            error = "无法读取目标显示器信息";
            return false;
        }

        var monitorRect = new Rectangle(
            info.rcMonitor.left,
            info.rcMonitor.top,
            info.rcMonitor.right - info.rcMonitor.left,
            info.rcMonitor.bottom - info.rcMonitor.top);

        if (monitorRect.Width != monitorWidth || monitorRect.Height != monitorHeight)
        {
            error = $"固定坐标仅支持 {monitorWidth}×{monitorHeight} 显示器，当前为 {monitorRect.Width}×{monitorRect.Height}";
            return false;
        }

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            error = "固定网格外框坐标无效";
            return false;
        }

        rect = new Rectangle(monitorRect.Left + left, monitorRect.Top + top, width, height);
        if (!monitorRect.Contains(rect.Left, rect.Top) ||
            !monitorRect.Contains(rect.Right - 1, rect.Bottom - 1))
        {
            rect = Rectangle.Empty;
            error = "固定网格区域超出目标显示器";
            return false;
        }

        return true;
    }

    /// <summary>按行优先计算格子中心，输入矩形的边界点不属于任何格子中心。</summary>
    public static List<Point> GetCellCenters(Rectangle outerRect, int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
            return [];

        var points = new List<Point>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var x = outerRect.Left + (int)Math.Round((col + 0.5) * outerRect.Width / (double)columns);
                var y = outerRect.Top + (int)Math.Round((row + 0.5) * outerRect.Height / (double)rows);
                points.Add(new Point(x, y));
            }
        }

        return points;
    }
}
