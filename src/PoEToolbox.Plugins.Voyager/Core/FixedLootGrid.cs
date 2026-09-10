using System.Drawing;
using PoEToolbox.Plugins.BagCleaner.Core;

namespace PoEToolbox.Plugins.Voyager.Core;

/// <summary>
/// 2K POE 屏幕下的海图仓库网格。
/// 坐标是相对于 POE 所在显示器左上角的外框顶点，不是格子中心。
/// </summary>
public static class FixedLootGrid
{
    // Keep the 2K profile separate so a future 1080P profile cannot alter it.
    public const string LayoutId = "2K (2560x1440)";
    public const int ScreenWidth = 2560;
    public const int ScreenHeight = 1440;
    public const int Left = 567;
    public const int Top = 212;
    public const int Right = 1110;
    public const int Bottom = 1110;
    public const int Columns = 12;
    public const int Rows = 20;

    public static int Width => Right - Left;
    public static int Height => Bottom - Top;

    /// <summary>
    /// 将固定的显示器内坐标转换成屏幕绝对矩形，并校验 POE 所在显示器。
    /// </summary>
    public static bool TryGetScreenRect(IntPtr poeHwnd, out Rectangle rect, out string error)
        => FixedScreenGrid.TryGetScreenRect(
            poeHwnd, ScreenWidth, ScreenHeight, Left, Top, Right, Bottom,
            out rect, out error);

    /// <summary>返回 2K (2560×1440) 布局下按行优先排列的 12×20 格子中心。</summary>
    public static List<Point> GetCellCenters(Rectangle rect)
        => FixedScreenGrid.GetCellCenters(rect, Columns, Rows);
}
