namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>
/// 可提取数值的地图词缀条目。
/// <para>
/// 固定枚举（而非字符串键）的理由：规则层要按条目配门槛，字符串键会让
/// 「拼错的关键词」静默变成「永远不命中」，而枚举拼错编译不过。
/// </para>
/// </summary>
public enum MapStat
{
    /// <summary>物品数量 %</summary>
    ItemQuantity = 0,

    /// <summary>物品稀有度 %</summary>
    ItemRarity,

    /// <summary>怪物群大小 %</summary>
    MonsterPackSize,

    /// <summary>更多地图 %</summary>
    MoreMaps,

    /// <summary>更多圣甲虫 %</summary>
    MoreScarabs,

    /// <summary>更多通货 %</summary>
    MoreCurrency,
}
