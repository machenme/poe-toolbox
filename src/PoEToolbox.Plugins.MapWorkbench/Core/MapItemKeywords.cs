namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>
/// 物品文本关键词表（简中 / 繁中 / 英文三语言并列）。
///
/// <para>
/// 词表自建，<b>不是</b>从外部工具（<c>ditu/</c>）的 <c>POEConst</c> 抄的——那些是反编译产物的字符串常量，
/// 来源与许可不明。此处只保留「同一条目给出三种语言的候选串」这一结构，串全部按客户端实际文案重录。
/// </para>
/// <para>
/// 三语言并列的理由与外部工具相同：客户端语言包决定 <c>Ctrl+C</c> 文本落在哪一列，
/// 而本模块不假设用户跑的是哪个语言客户端。命中判定一律「任一语言串出现即真」，
/// 不做语言协商——同一份文本里不会同时出现两种语言的同一词条，并列不会互相干扰。
/// </para>
/// <para>
/// <b>客户端的排版空格</b>：简中/繁中的稀有度行形如「稀 有 度: 普通」，冒号前有空格、
/// 字与字之间有空格。这些空格随客户端版本变过，所以稀有度一律用「冒号后的取值词」判定，
/// 不用整行匹配（见 <see cref="NormalRarity"/> 等）。
/// </para>
/// </summary>
internal static class MapItemKeywords
{
    // ═══ 稀有度（取冒号后的词，不匹配整行） ═══════════════

    internal static readonly string[] NormalRarity = ["普通", "普通", "Normal"];
    internal static readonly string[] MagicRarity = ["魔法", "魔法", "Magic"];
    internal static readonly string[] RareRarity = ["稀有", "稀有", "Rare"];
    internal static readonly string[] UniqueRarity = ["传奇", "傳奇", "Unique"];

    // ═══ 状态位 ═══════════════════════════════════════════

    internal static readonly string[] Unidentified = ["未鉴定", "未鑑定", "Unidentified"];
    internal static readonly string[] Corrupted = ["已腐化", "已汙染", "Corrupted"];
    internal static readonly string[] Mirrored = ["已复制", "已複製", "Mirrored"];

    // ═══ 物品类别 ═════════════════════════════════════════

    internal static readonly string[] Map = ["地图", "地圖", "Map"];

    // ═══ 地图词缀（数值提取的目标） ═══════════════════════
    // 顺序即优先级：GetStat 取第一个命中的条目。这里不存在互为子串的条目，
    // 但「物品数量」与「物品稀有度」都以「物品」开头，逐块匹配（非整段 Contains）是正确性的前提。

    internal static readonly string[] ItemQuantity = ["物品数量", "物品數量", "Item Quantity"];
    internal static readonly string[] ItemRarity = ["物品稀有度", "物品稀有度", "Item Rarity"];
    internal static readonly string[] MonsterPackSize = ["怪物群大小", "怪物群大小", "Monster Pack Size"];
    internal static readonly string[] MoreMaps = ["更多地图", "更多地圖", "More Maps"];
    internal static readonly string[] MoreScarabs = ["更多圣甲虫", "更多聖甲蟲", "More Scarabs"];
    internal static readonly string[] MoreCurrency = ["更多通货", "更多通貨", "More Currency"];

    // ═══ 其它属性 ═════════════════════════════════════════

    internal static readonly string[] Quality = ["品质", "品質", "Quality"];
    internal static readonly string[] ItemLevel = ["物品等级", "物品等級", "Item Level"];
    internal static readonly string[] MapTier = ["地图阶级", "地圖階級", "Map Tier"];

    // ═══ 通货名（校准页回显「这一格识别到的是什么」用） ═══

    internal static readonly string[] ScrollOfWisdom = ["知识卷轴", "知識卷軸", "Scroll of Wisdom"];
    internal static readonly string[] CartographerChisel = ["制图钉", "製圖釘", "Cartographer's Chisel"];
    internal static readonly string[] OrbOfTransmutation = ["蜕变石", "蛻變石", "Orb of Transmutation"];
    internal static readonly string[] OrbOfAlteration = ["改造石", "改造石", "Orb of Alteration"];
    internal static readonly string[] OrbOfAugmentation = ["增幅石", "增幅石", "Orb of Augmentation"];
    internal static readonly string[] OrbOfAlchemy = ["点金石", "點金石", "Orb of Alchemy"];
    internal static readonly string[] OrbOfScouring = ["重铸石", "重鑄石", "Orb of Scouring"];
    internal static readonly string[] ChaosOrb = ["混沌石", "混沌石", "Chaos Orb"];
    internal static readonly string[] ExaltedOrb = ["崇高石", "崇高石", "Exalted Orb"];
    internal static readonly string[] RegalOrb = ["富豪石", "富豪石", "Regal Orb"];
    internal static readonly string[] VaalOrb = ["瓦尔宝珠", "瓦爾寶珠", "Vaal Orb"];

    /// <summary>命中判定：任一语言串出现即真。空文本恒假。</summary>
    internal static bool MatchesAny(string text, string[] candidates)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in candidates)
            if (text.Contains(c, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>返回第一个命中的候选串（用于回显与日志），无命中返回 null。</summary>
    internal static string? FirstMatch(string text, string[] candidates)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var c in candidates)
            if (text.Contains(c, StringComparison.Ordinal)) return c;
        return null;
    }
}
