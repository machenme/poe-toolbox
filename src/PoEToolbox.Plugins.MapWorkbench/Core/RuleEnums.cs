namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>用户手填词缀条目的作用方向。对应 <see cref="ParsedItem.AffixBlocks"/> 的逐块匹配。</summary>
public enum AffixKind
{
    /// <summary>排除：命中即否决，优先于其它一切条件。</summary>
    Exclude = 0,

    /// <summary>必须包含。</summary>
    Require,
}

/// <summary>多条 <see cref="AffixKind.Require"/> 条目之间的满足方式。</summary>
public enum IncludeMode
{
    /// <summary>全部满足。</summary>
    AllRequire = 0,

    /// <summary>满足任一即可。</summary>
    AnyRequire,
}

/// <summary>数值条件的比较方式。</summary>
public enum MatchMode
{
    /// <summary>全部启用的条件都要达标。</summary>
    All = 0,

    /// <summary>任一启用的条件达标即可。</summary>
    Any,

    /// <summary>各启用条件的数值之和达到下限。</summary>
    Total,
}
