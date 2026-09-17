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
    /// <summary>方案自己的颜色（含由它合成的色阶前缀）。</summary>
    private static IReadOnlyList<AffixColorDef> _owned = [];

    /// <summary>游戏里已存在、不属于方案的颜色（第三方补丁定义的）。只做兜底查询。</summary>
    private static IReadOnlyList<AffixColorDef> _external = [];

    public static IReadOnlyDictionary<string, Color> Colors { get; private set; }
        = new Dictionary<string, Color>();

    /// <summary>重建调色板。色阶前缀（如 <c>AT</c>）会映射到它首档的颜色，供列表预览按色阶着色。</summary>
    public static void Update(IEnumerable<AffixColorDef> defs)
    {
        _owned = defs as IReadOnlyList<AffixColorDef> ?? [.. defs];
        Apply();
    }

    /// <summary>登记「别的补丁已经定义好」的颜色，让列表能把它们渲染出来。
    /// 刻意<b>不</b>参与色阶合成：外部色往往成系列（AT1~AT4、DA1~DA4），
    /// 合成后界面会凭空多出一批不属于本方案的色阶。</summary>
    public static void SetExternal(IEnumerable<AffixColorDef> external)
    {
        _external = external as IReadOnlyList<AffixColorDef> ?? [.. external];
        Apply();
    }

    private static void Apply()
    {
        var map = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var def in _owned)
            map[def.Id] = Color.FromArgb(def.A, def.R, def.G, def.B);
        foreach (var ramp in AffixColorRamp.FromColors(_owned))
        {
            if (map.TryGetValue(ramp.ColorIds[0], out var color))
                map[ramp.Prefix] = color;
        }
        // 外部色兜底：同名以方案为准，方案没有的才用游戏里已有的定义
        foreach (var def in _external)
            map.TryAdd(def.Id, Color.FromArgb(def.A, def.R, def.G, def.B));
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
