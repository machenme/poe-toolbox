using System.Collections;
using System.Globalization;

namespace PoEToolbox.Core.Binary.Datc64;

/// <summary>
/// 比较两个 dat 行值是否「语义相等」——用于往返校验、两份索引对比、改动前后比对。
///
/// <para>
/// 为什么不用 <c>Equals</c>：数组列的值是 <c>List&lt;object&gt;</c>，<c>Equals</c> 比引用，
/// 会造成几百处假漂移。历史上因此改用 <c>JsonSerializer.Serialize</c> 做深比较。
/// </para>
/// <para>
/// 为什么现在不再用 <c>JsonSerializer</c>：反射序列化在 Native AOT 下被禁用，运行时直接抛
/// <c>InvalidOperationException</c>（实测于 probe-endgamemaps 的语义比对步骤）。这里把等价语义
/// 显式实现出来，不依赖序列化，同时省掉中间字符串的分配。
/// </para>
/// </summary>
public static class DatRowValueComparer
{
    /// <summary>逐层比较两个值。数组按长度 + 逐元素比较。</summary>
    public static bool AreEqual(object? a, object? b)
    {
        if (a is null) return b is null;
        if (b is null) return false;

        if (a is string sa)
            return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);

        if (a is bool ba)
            return b is bool bb && ba == bb;

        // 数组列 / interval 列：值是 List<object?>，必须逐元素比，不能比引用。
        if (a is IList la)
            return b is IList lb && AreListsEqual(la, lb);

        // 数值走 round-trip 文本比较，等价于 System.Text.Json 的 number 语义。
        if (TryNumericText(a, out var ta) && TryNumericText(b, out var tb))
            return ta == tb;

        return Equals(a, b);
    }

    /// <summary>
    /// 把值渲染成一小段文本，供「哪里不一样」的诊断输出使用。故意走 <see cref="AreEqual"/>
    /// 相同的折算口径（数值同样按 round-trip 文本），这样报出来的两边是可直接对照的。
    /// </summary>
    /// <param name="value">待渲染的值。</param>
    /// <param name="maxLen">最长字符数，超出截断。</param>
    public static string Describe(object? value, int maxLen = 60)
    {
        var text = Render(value);
        return text.Length <= maxLen ? text : text[..maxLen];
    }

    private static bool AreListsEqual(IList a, IList b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!AreEqual(a[i], b[i]))
                return false;
        }
        return true;
    }

    private static bool TryNumericText(object value, out string text)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong
                 or float or double or decimal:
                // 刻意不先提升成 double：(float)0.1 一旦变成 double 就是 0.100000001490116，
                // 与另一侧的 double 0.1 会被误判为不同。各自的 ToString 都是最短往返表达，
                // 于是 float 0.1 与 double 0.1 都得到 "0.1"，与 JSON number 的语义一致。
                text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }

    private static string Render(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case IList list:
            {
                // 只渲染前若干个元素：诊断输出不需要把整列都打印出来。
                const int maxItems = 8;
                var shown = Math.Min(list.Count, maxItems);
                var parts = new string[shown + (list.Count > maxItems ? 1 : 0)];
                for (var i = 0; i < shown; i++)
                {
                    var item = Render(list[i]);
                    parts[i] = item.Length > 24 ? item[..24] : item;
                }
                if (list.Count > maxItems) parts[shown] = "...";
                return "[" + string.Join(", ", parts) + "]";
            }
            default:
                return TryNumericText(value, out var text) ? text : value.ToString() ?? string.Empty;
        }
    }
}
