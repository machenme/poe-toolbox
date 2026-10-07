namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>物品稀有度。<see cref="Unknown"/> 表示文本里没有可识别的稀有度行（非地图物品、或格式变化）。</summary>
public enum ItemRarity
{
    Unknown = 0,
    Normal,
    Magic,
    Rare,
    Unique,
}

/// <summary>
/// 单张物品的解析结果（纯内存，不持久化）。
///
/// <para>
/// 核心约定（本模块与外部工具最大的分歧点）：<see cref="Stats"/> 里
/// <b>「键不存在」= 该词缀没出现或没解析出来；「值为 0」= 确实解析到了 0</b>。
/// 外部工具把两者混为一谈（解析失败静默返回 0），于是「词缀没匹配上」和
/// 「词缀真是 0%」在界面上完全无法区分——那是它最隐蔽的误判来源。这里不重蹈。
/// </para>
/// <para>
/// 因此取数值请一律用 <see cref="TryGetStat"/>；<b>不要</b>用 <c>Stats.GetValueOrDefault</c>
/// 之类的写法，那会把「缺失」重新折叠成 0，等于把上面那条约定作废。
/// </para>
/// </summary>
public sealed class ParsedItem
{
    /// <summary>物品原文，供正则模式与日志复盘。</summary>
    public string RawText { get; init; } = string.Empty;

    public ItemRarity Rarity { get; init; } = ItemRarity.Unknown;

    /// <summary>是否地图（命中地图关键词）。</summary>
    public bool IsMap { get; init; }

    /// <summary>是否已鉴定（未命中「未鉴定」即视为已鉴定）。</summary>
    public bool IsIdentified { get; init; } = true;

    public bool IsCorrupted { get; init; }
    public bool IsMirrored { get; init; }

    /// <summary>地图阶级；文本里没有则为 null。</summary>
    public int? MapTier { get; init; }

    /// <summary>品质百分比；文本里没有则为 null。</summary>
    public int? Quality { get; init; }

    /// <summary>物品等级；文本里没有则为 null。</summary>
    public int? ItemLevel { get; init; }

    /// <summary>按花括号分块后的词缀原始文本（不含外层花括号，已 Trim）。</summary>
    public IReadOnlyList<string> AffixBlocks { get; init; } = [];

    /// <summary>词缀条数 = <see cref="AffixBlocks"/> 的长度。</summary>
    public int AffixCount => AffixBlocks.Count;

    /// <summary>
    /// 关键词 → 数值。键取自 <see cref="MapStat"/> 的固定集合，值可为负。
    /// 解析失败的条目<b>不入字典</b>（见类型注释）。
    /// </summary>
    public IReadOnlyDictionary<MapStat, int> Stats { get; init; } =
        new Dictionary<MapStat, int>();

    /// <summary>取数值；未解析到返回 false（区别于解析到 0）。</summary>
    public bool TryGetStat(MapStat stat, out int value) => Stats.TryGetValue(stat, out value);

    /// <summary>取数值，缺省返回 <paramref name="fallback"/>。仅用于「缺失时按某值处理」是明确意图的场合。</summary>
    public int GetStatOr(MapStat stat, int fallback)
        => Stats.TryGetValue(stat, out var v) ? v : fallback;

    /// <summary>是否可被洗练处理（地图 + 未复制 + 未腐化）。对齐外部工具的 IsAvailableMap 语义。</summary>
    public bool IsPolishable => IsMap && !IsMirrored && !IsCorrupted;

    /// <summary>词缀文本里是否含指定子串（逐块匹配，不跨词缀）。</summary>
    public bool HasAffixContaining(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return false;
        foreach (var block in AffixBlocks)
            if (block.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }
}
