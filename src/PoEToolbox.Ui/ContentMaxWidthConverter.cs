using System;
using System.Globalization;
using System.Windows.Data;

namespace PoEToolbox.Ui;

/// <summary>
/// 把「父容器给的可用宽度」换算成内容区的封顶宽度，<b>按比例而非绝对像素</b>。
///
/// <para>
/// 2026-10-07 的问题：内容区那一层写成 <c>MaxWidth="1600"</c>，
/// 主人一眼看出「用户分辨率不同，写死数值会出问题」。实测确实如此：
/// </para>
/// <list type="bullet">
/// <item>1546 → 可用 1282 → 浪费 0（刚好没到封顶，看不出问题）</item>
/// <item>2560 → 可用 2296 → <b>浪费 696px（30.3%）</b></item>
/// <item>3440 → 可用 3176 → <b>浪费 1576px（49.6%）</b></item>
/// </list>
///
/// <para>
/// <b>但也不能无脑乘个系数</b>。试过 <c>可用 × 0.92</c>，浪费比例确实恒定了，
/// 可主人当前窗口（可用 1282）也跟着浪费 103px —— 而那正是他刚确认过、
/// 觉得「铺满就挺好」的观感，不能为了照顾 4K 把 1080p 也拉下水。
/// </para>
///
/// <para>
/// 所以这里用<b>分段线性</b>策略，阈值同样不写死：
/// </para>
/// <list type="number">
/// <item>可用宽度 ≤ <see cref="ComfortWidth"/>：<b>不封顶</b>，完全铺满（返回 available）。
/// 这是绝大多数用户（1080p 及以下）的情形，观感与「无 MaxWidth」完全一致。</item>
/// <item>超过 <see cref="ComfortWidth"/>：<c>cap = comfort + ratio × (available - comfort)</c>。
/// 在 available = comfort 处与上一支连续（不会跳变），斜率 < 1（增速放缓），
/// available → ∞ 时 cap ≈ ratio × available，浪费比例收敛到 (1 - ratio)。</item>
/// </list>
///
/// <para>
/// 默认 <c>ComfortWidth = 1600</c> 只是「从哪儿开始收窄」的转折点，
/// 不是「最多只能这么宽」——和原先写死 <c>MaxWidth="1600"</c> 的关键区别：
/// 那个值一旦超过就永久截断（4K 上浪费一半 49.6%），
/// 这里 1600 以上的宽度仍然会继续增长，只是增速放缓。
/// </para>
/// </summary>
public sealed class ContentMaxWidthConverter : IValueConverter
{
    /// <summary>
    /// 舒适区宽度：可用宽度不超过它就完全铺满，超过才开始平滑收窄。
    /// 默认 1600（对应 1080p 略宽一点的窗口）。
    /// </summary>
    public double ComfortWidth { get; set; } = 1600;

    /// <summary>最宽时（可用宽度趋于无穷）保留的可用比例。</summary>
    public double Ratio { get; set; } = 0.92;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 入参是父容器给的可用宽度（double）。拿不到时返回 PositiveInfinity，
        // 意思是「不设上限」——宁可铺满，也不要因为算不出宽度就退化成 0。
        if (value is not double available || double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
            return double.PositiveInfinity;

        var comfort = ComfortWidth;
        var ratio = Ratio;
        if (parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
        {
            // 允许 ConverterParameter 传 "舒适宽,比例" 覆盖默认值。
            var parts = s.Split(',');
            comfort = a;
            if (parts.Length > 1
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                ratio = b;
        }

        if (available <= comfort)
            return available;   // 舒适区：完全铺满

        // 舒适区之外：cap = comfort + ratio × (available - comfort)。
        //   · available = comfort 时 cap = comfort，与上面那一支连续（不会跳变）；
        //   · ratio < 1 ⇒ cap ≤ comfort + (available - comfort) = available，永远不超宽；
        //   · 单调递增，且 available → ∞ 时 cap ≈ ratio × available，
        //     浪费比例收敛到 (1 - ratio) 而不是无限扩大。
        return comfort + (available - comfort) * ratio;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("ContentMaxWidthConverter 是单向的");
}
