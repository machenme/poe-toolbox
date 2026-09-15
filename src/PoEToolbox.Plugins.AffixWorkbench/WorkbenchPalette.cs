using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench;

/// <summary>
/// 当前方案颜色 id → Color 的注册表：列表预览与色块通过绑定/converter 读取，
/// 方案变更时整体替换并刷新视图。
/// </summary>
public static class WorkbenchPalette
{
    public static IReadOnlyDictionary<string, Color> Colors { get; private set; }
        = new Dictionary<string, Color>();

    /// <summary>重建调色板。色阶前缀（如 <c>AT</c>）会映射到它首档的颜色，供列表预览按色阶着色。</summary>
    public static void Update(IEnumerable<AffixColorDef> defs)
    {
        var colors = defs as IList<AffixColorDef> ?? [.. defs];
        var map = colors.ToDictionary(c => c.Id, c => Color.FromArgb(c.A, c.R, c.G, c.B), StringComparer.Ordinal);
        foreach (var ramp in AffixColorRamp.FromColors(colors))
        {
            if (map.TryGetValue(ramp.ColorIds[0], out var color))
                map[ramp.Prefix] = color;
        }
        Colors = map;
    }

    public static bool TryGet(string? id, out Color color)
    {
        color = default;
        return id is not null && Colors.TryGetValue(id, out color);
    }
}

/// <summary>颜色 id → 前景画刷；未上色 / 未登记的 id 返回 UnsetValue，
/// 回退到主题默认文字色（此前回退透明色会导致未上色词缀的预览文本不可见）。</summary>
public sealed class AffixColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (WorkbenchPalette.TryGet(value as string, out var color))
            return new SolidColorBrush(color);
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
