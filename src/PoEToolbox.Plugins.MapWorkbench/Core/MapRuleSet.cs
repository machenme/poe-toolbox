namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>
/// 筛选规则：判定一张已鉴定的稀有地图是否「达标」（值得强化并存入仓库）。
///
/// <para>
/// 这是纯数据，规则层（<see cref="RuleEngine"/>）只读它。字段名与 <c>config.json</c>
/// 的 <c>MapWorkbench</c> 段一致，由 <c>ConfigService</c> 直接序列化。
/// </para>
/// <para>
/// <b>设计取舍</b>：<see cref="Affixes"/> 是用户手填关键词，本模块<b>不内置词缀表</b>。
/// 好处是官方每赛季改词缀池时本模块零改动；代价是用户要自己敲，
/// 且关键词是子串匹配、不区分词缀与档位——档位筛选由 <see cref="Thresholds"/> 承担。
/// </para>
/// </summary>
public sealed class MapRuleSet
{
    /// <summary>排除 / 必须包含 条目。关键词不可为空（空的会被规则层忽略）。</summary>
    public List<MapAffixRule> Affixes { get; set; } = [];

    /// <summary><see cref="AffixKind.Require"/> 条目之间的满足方式。</summary>
    public IncludeMode IncludeMode { get; set; } = IncludeMode.AnyRequire;

    /// <summary>数值门槛（null = 该条件不启用）。</summary>
    public MapStatThresholds Thresholds { get; set; } = new();

    /// <summary>数值条件的比较方式。</summary>
    public MatchMode MatchMode { get; set; } = MatchMode.Any;

    /// <summary><see cref="MatchMode.Total"/> 时，各启用项数值之和的下限。</summary>
    public int MatchTotalMin { get; set; }

    /// <summary>
    /// 正则逃生舱。非空且启用时<b>完全接管</b>判定，数值条件全部短路取消
    /// （与外部工具同语义：勾了正则就不再算数量/稀有度等）。
    /// 由 <see cref="RuleEngine"/> 负责编译，编译失败视为「规则不可用」而非「全部命中」。
    /// </summary>
    public string? MapRegex { get; set; }
}

/// <summary>单条词缀规则。</summary>
public sealed class MapAffixRule
{
    /// <summary>关键词，逐块子串匹配（大小写敏感，与游戏文案一致）。</summary>
    public string Keyword { get; set; } = string.Empty;

    public AffixKind Kind { get; set; } = AffixKind.Require;
}

/// <summary>数值门槛集合。可空 int 作为「未启用」的表达——null 不是 0。</summary>
public sealed class MapStatThresholds
{
    public int? ItemQuantity { get; set; }
    public int? ItemRarity { get; set; }
    public int? MonsterPackSize { get; set; }
    public int? MoreMaps { get; set; }
    public int? MoreScarabs { get; set; }
    public int? MoreCurrency { get; set; }

    /// <summary>该门槛对应的词缀条目。</summary>
    public int? this[MapStat stat] => stat switch
    {
        MapStat.ItemQuantity => ItemQuantity,
        MapStat.ItemRarity => ItemRarity,
        MapStat.MonsterPackSize => MonsterPackSize,
        MapStat.MoreMaps => MoreMaps,
        MapStat.MoreScarabs => MoreScarabs,
        MapStat.MoreCurrency => MoreCurrency,
        _ => null,
    };

    /// <summary>已启用的 (词缀, 门槛) 序列。判定一律走这里，保证「启用」的口径只有一处。</summary>
    public IEnumerable<(MapStat Stat, int Min)> Enabled()
    {
        foreach (var stat in Enum.GetValues<MapStat>())
        {
            var min = this[stat];
            if (min.HasValue) yield return (stat, min.Value);
        }
    }
}
