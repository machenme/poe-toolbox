using System.Text.RegularExpressions;

namespace PoEToolbox.Plugins.MapWorkbench.Core;

/// <summary>判定结果，含中文原因（视图与日志直接用，不再二次拼装）。</summary>
public readonly record struct RuleVerdict(bool Passed, string Reason)
{
    public static RuleVerdict Pass(string reason) => new(true, reason);
    public static RuleVerdict Fail(string reason) => new(false, reason);
}

/// <summary>
/// 筛选规则判定。纯函数、无 IO、无静态可变状态（SPEC §5）。
///
/// <para>判定顺序（顺序即语义，改动前先看 <c>RuleEngineTests</c>）：</para>
/// <list type="number">
/// <item>非稀有 → 直接否决（只有稀有图才谈得上「达标」）。</item>
/// <item>排除词命中 → 否决。优先于一切，含正则模式。</item>
/// <item>正则启用且可编译 → 只由正则决定，数值条件全部短路。</item>
/// <item>必须包含词（全含 / 任一含）。</item>
/// <item>数值门槛（全部 / 任一 / 总和）。</item>
/// </list>
///
/// <para>
/// 与外部工具的一处刻意差异：它的排除词判定在<b>正则之前但整体 Contains 全文</b>，
/// 于是排除词若恰好出现在物品名或说明里也会误杀。本实现用
/// <see cref="ParsedItem.HasAffixContaining"/>（逐词缀块）限定范围。
/// </para>
/// </summary>
public static class RuleEngine
{
    /// <summary>判定一张物品是否达标。<paramref name="item"/> 为空或未解析出稀有度时否决。</summary>
    public static RuleVerdict Evaluate(ParsedItem? item, MapRuleSet? rules)
    {
        if (item is null) return RuleVerdict.Fail("没有物品");
        rules ??= new MapRuleSet();

        // ① 只有稀有图才谈「达标」——普通/魔法图应该被洗掉，而不是被当成好图强化。
        if (item.Rarity != ItemRarity.Rare)
            return RuleVerdict.Fail($"非稀有物品（{Describe(item.Rarity)}）");

        var excludes = ActiveRules(rules, AffixKind.Exclude);
        var requires = ActiveRules(rules, AffixKind.Require);

        // ② 排除词优先于一切，含正则模式。
        foreach (var rule in excludes)
            if (item.HasAffixContaining(rule.Keyword))
                return RuleVerdict.Fail($"命中排除词「{rule.Keyword}」");

        // ③ 正则逃生舱：可编译则完全接管。
        if (!string.IsNullOrWhiteSpace(rules.MapRegex))
        {
            if (!TryCompile(rules.MapRegex, out var regex, out var error))
                return RuleVerdict.Fail($"正则无效：{error}");

            return regex!.IsMatch(item.RawText)
                ? RuleVerdict.Pass("命中正则")
                : RuleVerdict.Fail("未命中正则");
        }

        // ④ 必须包含词。
        if (requires.Count > 0)
        {
            if (rules.IncludeMode == IncludeMode.AllRequire)
            {
                foreach (var rule in requires)
                    if (!item.HasAffixContaining(rule.Keyword))
                        return RuleVerdict.Fail($"缺少必需词缀「{rule.Keyword}」");
            }
            else
            {
                bool any = false;
                foreach (var rule in requires)
                    if (item.HasAffixContaining(rule.Keyword)) { any = true; break; }

                if (!any)
                    return RuleVerdict.Fail(
                        $"未命中任一必需词缀（{string.Join(" / ", requires.Select(r => r.Keyword))}）");
            }
        }

        // ⑤ 数值门槛。
        var enabled = rules.Thresholds.Enabled().ToList();
        if (enabled.Count == 0)
            return RuleVerdict.Pass(requires.Count > 0 ? "词缀条件满足" : "无数值条件，默认通过");

        return rules.MatchMode switch
        {
            MatchMode.All => EvaluateAll(item, enabled),
            MatchMode.Any => EvaluateAny(item, enabled),
            MatchMode.Total => EvaluateTotal(item, enabled, rules.MatchTotalMin),
            _ => RuleVerdict.Fail($"未知匹配模式 {rules.MatchMode}"),
        };
    }

    // ═══ 三种数值模式 ═════════════════════════════════════

    /// <summary>全部达标。缺失的词缀按「不达标」处理——这是它与「值为 0」的分野。</summary>
    private static RuleVerdict EvaluateAll(ParsedItem item, List<(MapStat Stat, int Min)> enabled)
    {
        var missing = new List<string>();
        var below = new List<string>();

        foreach (var (stat, min) in enabled)
        {
            if (!item.TryGetStat(stat, out int actual)) { missing.Add(Label(stat)); continue; }
            if (actual < min) below.Add($"{Label(stat)} {actual} < {min}");
        }

        if (missing.Count > 0)
            return RuleVerdict.Fail($"缺少词缀（{string.Join(" / ", missing)}）");
        if (below.Count > 0)
            return RuleVerdict.Fail($"数值不足（{string.Join("；", below)}）");

        return RuleVerdict.Pass("全部数值达标");
    }

    /// <summary>任一达标。缺失的条目自然算不达标，不单独报错。</summary>
    private static RuleVerdict EvaluateAny(ParsedItem item, List<(MapStat Stat, int Min)> enabled)
    {
        int matched = 0;
        foreach (var (stat, min) in enabled)
            if (item.TryGetStat(stat, out int actual) && actual >= min) matched++;

        return matched > 0
            ? RuleVerdict.Pass($"命中 {matched} 项数值条件")
            : RuleVerdict.Fail("没有任何一项数值达标");
    }

    /// <summary>数值之和达标。缺失的条目按 0 计入和——这里折叠成 0 是明确意图（求和语义），不是误判。</summary>
    private static RuleVerdict EvaluateTotal(
        ParsedItem item, List<(MapStat Stat, int Min)> enabled, int totalMin)
    {
        int sum = 0;
        var parts = new List<string>();

        foreach (var (stat, _) in enabled)
        {
            int v = item.GetStatOr(stat, 0);
            sum += v;
            parts.Add($"{Label(stat)} {v}");
        }

        return sum >= totalMin
            ? RuleVerdict.Pass($"数值合计 {sum} ≥ {totalMin}（{string.Join(" + ", parts)}）")
            : RuleVerdict.Fail($"数值合计 {sum} < {totalMin}（{string.Join(" + ", parts)}）");
    }

    // ═══ 辅助 ═════════════════════════════════════════════

    private static List<MapAffixRule> ActiveRules(MapRuleSet rules, AffixKind kind)
        => rules.Affixes
            .Where(r => r.Kind == kind && !string.IsNullOrWhiteSpace(r.Keyword))
            .ToList();

    /// <summary>编译正则；失败时给出中文原因，调用方据此报「规则不可用」而非默默放行。</summary>
    private static bool TryCompile(string pattern, out Regex? regex, out string error)
    {
        try
        {
            // 加超时：用户写的灾难性回溯正则不能把洗图线程卡死。
            regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = ex.Message;
            return false;
        }
    }

    private static string Describe(ItemRarity r) => r switch
    {
        ItemRarity.Normal => "普通",
        ItemRarity.Magic => "魔法",
        ItemRarity.Rare => "稀有",
        ItemRarity.Unique => "传奇",
        _ => "未知",
    };

    internal static string Label(MapStat stat) => stat switch
    {
        MapStat.ItemQuantity => "物品数量",
        MapStat.ItemRarity => "物品稀有度",
        MapStat.MonsterPackSize => "怪物群大小",
        MapStat.MoreMaps => "更多地图",
        MapStat.MoreScarabs => "更多圣甲虫",
        MapStat.MoreCurrency => "更多通货",
        _ => stat.ToString(),
    };
}
