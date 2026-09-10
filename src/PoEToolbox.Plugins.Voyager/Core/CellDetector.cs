using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PoEToolbox.Plugins.BagCleaner.Input;
using static PoEToolbox.Plugins.BagCleaner.Input.NativeMethods;

namespace PoEToolbox.Plugins.Voyager.Core;

/// <summary>
/// 基于 Bitmap 像素标准差判空的格子检测器。
/// 截取仓库区域 → 切成 N×M 格 → 每格灰度标准差判空 → 返回占用布尔数组。
/// </summary>
public static class CellDetector
{
    /// <summary>标准差阈值：低于此值判定为空格。</summary>
    private const double StdDevThreshold = 15.0;

    /// <summary>单元格内缩百分比，排除边框线干扰。</summary>
    private const int InsetPercent = 5;

    /// <summary>
    /// 识别固定 2K 海图仓库。外框坐标来自 POE 所在显示器，不依赖校准配置。
    /// </summary>
    public static bool[] DetectFixed(IntPtr poeHwnd, out string error)
    {
        error = string.Empty;
        if (!FixedLootGrid.TryGetScreenRect(poeHwnd, out var gridRect, out error))
            return new bool[FixedLootGrid.Columns * FixedLootGrid.Rows];

        try
        {
            return DetectRectangle(gridRect, FixedLootGrid.Columns, FixedLootGrid.Rows);
        }
        catch (Exception ex)
        {
            error = $"截取海图仓库失败: {ex.Message}";
            return new bool[FixedLootGrid.Columns * FixedLootGrid.Rows];
        }
    }

    /// <summary>
    /// 截取 POE 窗口网格区域，逐格判空。保留给旧的可校准网格调用方。
    /// </summary>
    public static bool[] Detect(IntPtr poeHwnd, int columns, int rows,
        double startX, double startY, double stepX, double stepY)
    {
        var result = new bool[rows * columns];
        if (poeHwnd == IntPtr.Zero) return result;
        if (!GetClientRect(poeHwnd, out var rc)) return result;

        int clientWidth = rc.right - rc.left;
        int clientHeight = rc.bottom - rc.top;
        if (clientWidth <= 0 || clientHeight <= 0) return result;

        var origin = new POINT { x = 0, y = 0 };
        if (!ClientToScreen(poeHwnd, ref origin)) return result;

        double cellWidth = stepX * clientWidth;
        double cellHeight = stepY * clientHeight;
        int gridLeft = origin.x + (int)Math.Round((startX - stepX / 2) * clientWidth);
        int gridTop = origin.y + (int)Math.Round((startY - stepY / 2) * clientHeight);
        int gridWidth = (int)Math.Round(columns * cellWidth);
        int gridHeight = (int)Math.Round(rows * cellHeight);

        if (gridWidth <= 0 || gridHeight <= 0) return result;
        return DetectRectangle(new Rectangle(gridLeft, gridTop, gridWidth, gridHeight), columns, rows);
    }

    private static bool[] DetectRectangle(Rectangle gridRect, int columns, int rows)
    {
        var result = new bool[rows * columns];

        using var bitmap = new Bitmap(gridRect.Width, gridRect.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(gridRect.Left, gridRect.Top, 0, 0,
            new Size(gridRect.Width, gridRect.Height));

        return AnalyzeBitmap(bitmap, columns, rows);
    }

    private static bool[] AnalyzeBitmap(Bitmap bitmap, int columns, int rows)
    {
        var result = new bool[rows * columns];
        var bitmapRect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(
            bitmapRect,
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        var stride = Math.Abs(bitmapData.Stride);
        var pixels = new byte[stride * bitmap.Height];

        try
        {
            Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);

            // Read the screenshot once. This avoids the PNG encode/decode
            // round-trip previously used to create an OpenCV Mat.
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var left = (int)Math.Round(col * bitmap.Width / (double)columns);
                    var top = (int)Math.Round(row * bitmap.Height / (double)rows);
                    var right = (int)Math.Round((col + 1) * bitmap.Width / (double)columns);
                    var bottom = (int)Math.Round((row + 1) * bitmap.Height / (double)rows);
                    var cellWidth = right - left;
                    var cellHeight = bottom - top;

                    var insetX = Math.Max(1, cellWidth * InsetPercent / 100);
                    var insetY = Math.Max(1, cellHeight * InsetPercent / 100);
                    var innerWidth = Math.Max(1, cellWidth - 2 * insetX);
                    var innerHeight = Math.Max(1, cellHeight - 2 * insetY);
                    if (left + insetX + innerWidth > bitmap.Width ||
                        top + insetY + innerHeight > bitmap.Height)
                        continue;

                    double sum = 0;
                    double sumSquares = 0;
                    var count = 0;

                    for (var y = top + insetY; y < top + insetY + innerHeight; y++)
                    {
                        var rowOffset = bitmapData.Stride >= 0
                            ? y * stride
                            : (bitmap.Height - 1 - y) * stride;

                        for (var x = left + insetX; x < left + insetX + innerWidth; x++)
                        {
                            var pixelOffset = rowOffset + x * 3;
                            var gray = 0.114 * pixels[pixelOffset]
                                       + 0.587 * pixels[pixelOffset + 1]
                                       + 0.299 * pixels[pixelOffset + 2];
                            sum += gray;
                            sumSquares += gray * gray;
                            count++;
                        }
                    }

                    if (count == 0) continue;
                    var mean = sum / count;
                    var variance = Math.Max(0, sumSquares / count - mean * mean);
                    result[row * columns + col] = Math.Sqrt(variance) >= StdDevThreshold;
                }
            }

            return result;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }
}
