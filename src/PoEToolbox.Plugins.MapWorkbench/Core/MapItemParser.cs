using System.Globalization;
using System.Text.RegularExpressions;

namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>
/// 把客户端 <c>Ctrl+C</c> 复制出来的物品文本解析成 <see cref="ParsedItem"/>。
/// 纯函数、无 IO、无 UI、无静态状态，因此可在没有游戏的情况下单测（SPEC §1「人类保留地」①）。
///
/// <para><b>为什么不照抄外部工具的做法</b>：它提取数值走的是
/// 「<c>IndexOf(关键词)</c> → <c>IndexOf("+")</c> → <c>IndexOf("%")</c> → <c>Substring</c>」。
/// 那条路有三处硬伤，本实现逐条针对性规避：</para>
/// <list type="number">
/// <item><b>负数会串行</b>：词缀是 <c>-10%</c> 时它从关键词往回找不到自己的符号，
/// 会命中<b>下一个</b>词缀的 <c>+</c>，解析出别的词缀的数值。→ 本实现按块解析，数值不可能跨块。</item>
/// <item><b>失败静默</b>：解析不出来时返回 0，与「真的是 0%」无法区分。→ 本实现失败就不入 <c>Stats</c>。</item>
/// <item><b>依赖 <c>+</c> 在 <c>%</c> 前</b>：格式一变（如 <c>20% 品质</c>、无百分号）就整体失效。→ 本实现用带符号正则，不假设符号位置。</item>
/// </list>
/// </summary>
public static partial class MapItemParser
{
    /// <summary>词缀包裹字符（客户端把每条词缀放在一对花括号里）。</summary>
    private const char AffixOpen = '{';
    private const char AffixClose = '}';

    /// <summary>
    /// 数值提取：匹配「可选的 +/−」+「数字（含小数）」+「可选的 %」。
    /// <para>
    /// 刻意<b>不</b>要求 <c>%</c> 存在：部分词缀（如「地图阶级: 16」）没有百分号，
    /// 而 <see cref="MapTier"/> 这类属性同样要取值。是否带百分号由调用方语义决定，
    /// 解析器只负责把数字读出来。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"([+-]?\d+(?:\.\d+)?)\s*%?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>地图阶级 / 物品等级这类「词条: 数值」形式，冒号后取数。</summary>
    [GeneratedRegex(@"[：:]\s*([+-]?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ColonNumberPattern();

    /// <summary>
    /// 解析物品文本。空文本返回一个全空的 <see cref="ParsedItem"/>（不抛异常）——
    /// 调用方用「空格子」这条正常路径处理它，异常应该留给真正的意外。
    /// </summary>
    public static ParsedItem Parse(string? rawText)
    {
        var text = rawText ?? string.Empty;
        if (text.Length == 0)
            return new ParsedItem { RawText = text };

        var blocks = SplitAffixBlocks(text);

        return new ParsedItem
        {
            RawText = text,
            Rarity = DetectRarity(text),
            IsMap = MapItemKeywords.MatchesAny(text, MapItemKeywords.Map),
            IsIdentified = !MapItemKeywords.MatchesAny(text, MapItemKeywords.Unidentified),
            IsCorrupted = MapItemKeywords.MatchesAny(text, MapItemKeywords.Corrupted),
            IsMirrored = MapItemKeywords.MatchesAny(text, MapItemKeywords.Mirrored),
            MapTier = ExtractColonNumberInBlocks(blocks, MapItemKeywords.MapTier)
                      ?? ExtractColonNumberInText(text, MapItemKeywords.MapTier),
            Quality = ExtractPercent(blocks, MapItemKeywords.Quality)
                      ?? ExtractPercentInText(text, MapItemKeywords.Quality),
            ItemLevel = ExtractColonNumberInText(text, MapItemKeywords.ItemLevel),
            AffixBlocks = blocks,
            Stats = ExtractStats(blocks, text),
        };
    }

    // ═══ 分块 ═════════════════════════════════════════════

    /// <summary>
    /// 按花括号切出词缀块，返回<b>块内</b>文本（不含花括号，已 Trim）。
    ///
    /// <para>
    /// 只做单层扫描，不递归、不配对嵌套：客户端词缀不会嵌套花括号，
    /// 遇到未闭合的 <c>{</c> 就丢弃该块（宁可少算一条，也不要凭空造出一条假词缀
    /// 让「6 词缀」判定误通过）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> SplitAffixBlocks(string text)
    {
        var blocks = new List<string>();
        int i = 0;

        while (i < text.Length)
        {
            int open = text.IndexOf(AffixOpen, i);
            if (open < 0) break;

            int close = text.IndexOf(AffixClose, open + 1);
            if (close < 0) break; // 未闭合：丢弃，不猜结尾

            var inner = text.AsSpan(open + 1, close - open - 1).Trim();
            if (inner.Length > 0)
                blocks.Add(inner.ToString());

            i = close + 1;
        }

        return blocks;
    }

    // ═══ 稀有度 ═══════════════════════════════════════════

    /// <summary>
    /// 判定稀有度。对齐外部工具「取冒号后的取值词」而非整行匹配的思路——
    /// 客户端在「稀 有 度」里加的空格随版本变过，整行匹配会碎。
    /// <para>
    /// 顺序要紧：<c>Unique</c> 与 <c>Rare</c> 等词互不为子串，但英文 <c>Magic</c>/<c>Normal</c>
    /// 在说明文本里也可能出现，所以判定范围限定在<b>前若干行</b>（头部信息区），
    /// 不全文 Contains，避免词缀描述里提到「普通」时误判稀有度。
    /// </para>
    /// </summary>
    internal static ItemRarity DetectRarity(string text)
    {
        var head = HeadLines(text, maxLines: 8);

        if (MapItemKeywords.MatchesAny(head, MapItemKeywords.UniqueRarity)) return ItemRarity.Unique;
        if (MapItemKeywords.MatchesAny(head, MapItemKeywords.RareRarity)) return ItemRarity.Rare;
        if (MapItemKeywords.MatchesAny(head, MapItemKeywords.MagicRarity)) return ItemRarity.Magic;
        if (MapItemKeywords.MatchesAny(head, MapItemKeywords.NormalRarity)) return ItemRarity.Normal;

        return ItemRarity.Unknown;
    }

    /// <summary>取文本前 N 行（稀有度行恒在头部）。</summary>
    private static string HeadLines(string text, int maxLines)
    {
        int count = 0, pos = 0;
        while (pos < text.Length && count < maxLines)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0) break;
            pos = nl + 1;
            count++;
        }
        return count >= maxLines ? text[..pos] : text;
    }

    // ═══ 数值 ═════════════════════════════════════════════

    /// <summary>
    /// 从词缀块里抽取全部可识别词缀的数值，再用属性行补齐块里没给出的条目。
    /// <para>
    /// 逐块查找：对每个块，问「这个块里有没有某条目标词缀的关键词」，命中则该块内取数。
    /// 因此一个块只贡献一条词缀的数值，跨块串行不可能发生。
    /// </para>
    /// <para>
    /// <b>为什么还要属性行兜底</b>：地图会把「物品数量 / 物品稀有度 / 怪物群大小」
    /// 作为属性直接印在信息区（形如 <c>物品数量: +28%</c>），而这三条是否<b>同时</b>
    /// 出现在花括号词缀里随客户端版本而定。<b>块优先、属性行只补缺</b>的优先级意味着：
    /// 两者都在时以词缀块为准（词缀语义明确），块里没有时才用属性行的数字。
    /// </para>
    /// <para>
    /// ⚠ 这条优先级是<b>待真实样本确认</b>的假设（见 SPEC §7）：若真机上属性行显示的是
    /// 「底图 + 品质 + 词缀」的<b>合计</b>，那么合计才是玩家关心的门槛口径，优先级应反过来。
    /// 改成 <c>PropertiesFirst</c> 是几行的事，但必须先有一份真实 <c>Ctrl+C</c> 样本再定，
    /// 不要凭猜测改。
    /// </para>
    /// </summary>
    internal static Dictionary<MapStat, int> ExtractStats(IReadOnlyList<string> blocks, string text)
    {
        var stats = new Dictionary<MapStat, int>();

        foreach (var block in blocks)
            foreach (var (stat, keywords) in StatKeywords)
            {
                // 同一词缀在多块出现时取首个命中，不覆盖——客户端不会重复同条词缀，
                // 真出现重复说明文本异常，此时保留第一次读到的值更好排查。
                if (stats.ContainsKey(stat)) continue;
                if (!MapItemKeywords.MatchesAny(block, keywords)) continue;

                if (TryExtractFirstNumber(block, out int value))
                    stats[stat] = value;
                // 命中关键词但读不出数字：不写 0，留给调用方判为「缺失」
            }

        // 属性行兜底：只补块里没有的条目。
        foreach (var line in EnumerateLines(text))
            foreach (var (stat, keywords) in StatKeywords)
            {
                if (stats.ContainsKey(stat)) continue;
                if (!MapItemKeywords.MatchesAny(line, keywords)) continue;

                if (TryExtractFirstNumber(line, out int value))
                    stats[stat] = value;
            }

        return stats;
    }

    /// <summary>关键词 → 枚举的映射表（与 <see cref="MapItemKeywords"/> 一一对应）。</summary>
    private static readonly (MapStat Stat, string[] Keywords)[] StatKeywords =
    [
        (MapStat.ItemQuantity, MapItemKeywords.ItemQuantity),
        (MapStat.ItemRarity, MapItemKeywords.ItemRarity),
        (MapStat.MonsterPackSize, MapItemKeywords.MonsterPackSize),
        (MapStat.MoreMaps, MapItemKeywords.MoreMaps),
        (MapStat.MoreScarabs, MapItemKeywords.MoreScarabs),
        (MapStat.MoreCurrency, MapItemKeywords.MoreCurrency),
    ];

    /// <summary>
    /// 取块内第一个数字。<b>带符号</b>——这是与外部工具最关键的一处差异：
    /// <c>-10%</c> 必须读出 −10，而不是跳过负号去读后面的正数。
    /// </summary>
    internal static bool TryExtractFirstNumber(string block, out int value)
    {
        value = 0;
        var m = NumberPattern().Match(block);
        if (!m.Success) return false;

        // 先按整数解析（绝大多数词缀）；失败再试小数（少数词缀带一位小数）。
        if (int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value))
            return true;

        if (double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double d))
        {
            value = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            return true;
        }

        return false;
    }

    /// <summary>在块集合里找带某关键词的条目，取冒号后的整数。</summary>
    private static int? ExtractColonNumberInBlocks(IReadOnlyList<string> blocks, string[] keywords)
    {
        foreach (var block in blocks)
        {
            // 块内通常没有冒号（冒号只出现在头部信息区），但「地图阶级」这类属性
            // 在部分客户端版本里也被花括号包着，所以两条路都要留。
            var m = ColonNumberPattern().Match(block);
            if (m.Success && MapItemKeywords.MatchesAny(block, keywords)
                && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int v))
                return v;
        }
        return null;
    }

    /// <summary>在单段文本里找带某关键词的行，取冒号后的整数。</summary>
    private static int? ExtractColonNumberInText(string text, string[] keywords)
    {
        foreach (var line in EnumerateLines(text))
        {
            if (!MapItemKeywords.MatchesAny(line, keywords)) continue;
            var m = ColonNumberPattern().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int v))
                return v;
        }
        return null;
    }

    /// <summary>在块集合里找带某关键词的条目，取百分数。</summary>
    private static int? ExtractPercent(IReadOnlyList<string> blocks, string[] keywords)
    {
        foreach (var block in blocks)
        {
            if (!MapItemKeywords.MatchesAny(block, keywords)) continue;
            if (TryExtractFirstNumber(block, out int v)) return v;
        }
        return null;
    }

    /// <summary>
    /// 在全文中找带某关键词的行，取百分数。
    /// 品质在多数客户端版本里<b>不在</b>花括号内（形如「品质: +20%」独立成行），
    /// 所以只有块内找不到时才回落到这里。只认关键词命中的行，不做全文取数，
    /// 避免把别处的数字当成品质。
    /// </summary>
    private static int? ExtractPercentInText(string text, string[] keywords)
    {
        foreach (var line in EnumerateLines(text))
        {
            if (!MapItemKeywords.MatchesAny(line, keywords)) continue;
            if (TryExtractFirstNumber(line, out int v)) return v;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        int pos = 0;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                if (pos < text.Length) yield return text[pos..];
                yield break;
            }
            yield return text[pos..nl];
            pos = nl + 1;
        }
    }
}
