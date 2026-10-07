using PoEToolbox.Plugins.MapWorkbench.Core;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 筛选规则判定（SPEC §6）。纯函数，样本在测试内联构造——
/// 规则层关心的是「给定解析结果，判定对不对」，与真机文本格式无关，
/// 所以这一组不依赖 <c>Fixtures/map-items</c>，也不阻塞于真机取样。
/// </summary>
public sealed class RuleEngineTests
{
    private static ParsedItem Rare(params string[] affixBlocks) => new()
    {
        RawText = "稀 有 度: 稀有\n" + string.Join("\n", affixBlocks.Select(b => "{ " + b + " }")),
        Rarity = ItemRarity.Rare,
        IsMap = true,
        AffixBlocks = affixBlocks,
        Stats = new Dictionary<MapStat, int>(),
    };

    private static ParsedItem RareWithStats(
        Dictionary<MapStat, int> stats, params string[] affixBlocks) => new()
    {
        RawText = "稀 有 度: 稀有",
        Rarity = ItemRarity.Rare,
        IsMap = true,
        AffixBlocks = affixBlocks,
        Stats = stats,
    };

    // ═══ 稀有度前置 ═══════════════════════════════════════

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Unique)]
    [InlineData(ItemRarity.Unknown)]
    public void Evaluate_NonRareItem_Fails_EvenWithNoRules(ItemRarity rarity)
    {
        var item = new ParsedItem { Rarity = rarity, IsMap = true };
        Assert.False(RuleEngine.Evaluate(item, new MapRuleSet()).Passed);
    }

    [Fact]
    public void Evaluate_NullItem_Fails()
        => Assert.False(RuleEngine.Evaluate(null, new MapRuleSet()).Passed);

    [Fact]
    public void Evaluate_NullRules_TreatedAsEmptyRuleSet()
        => Assert.True(RuleEngine.Evaluate(Rare("+10% 物品数量"), null).Passed);

    // ═══ 排除词 ═══════════════════════════════════════════

    [Fact]
    public void Evaluate_ExcludeKeywordHit_Fails()
    {
        var rules = new MapRuleSet
        {
            Affixes = [new MapAffixRule { Keyword = "无法回复", Kind = AffixKind.Exclude }],
        };

        Assert.False(RuleEngine.Evaluate(Rare("玩家无法回复生命"), rules).Passed);
    }

    [Fact]
    public void Evaluate_ExcludeKeywordMiss_Passes()
    {
        var rules = new MapRuleSet
        {
            Affixes = [new MapAffixRule { Keyword = "无法回复", Kind = AffixKind.Exclude }],
        };

        Assert.True(RuleEngine.Evaluate(Rare("+10% 物品数量"), rules).Passed);
    }

    /// <summary>排除词优先于正则：正则再匹配，命中排除词也要否决。</summary>
    [Fact]
    public void Evaluate_ExcludeBeatsRegex()
    {
        var rules = new MapRuleSet
        {
            Affixes = [new MapAffixRule { Keyword = "反射", Kind = AffixKind.Exclude }],
            MapRegex = "稀有",
        };

        var item = new ParsedItem
        {
            RawText = "稀 有 度: 稀有\n{ 元素反射 }",
            Rarity = ItemRarity.Rare,
            AffixBlocks = ["元素反射"],
        };

        Assert.False(RuleEngine.Evaluate(item, rules).Passed);
    }

    /// <summary>排除词只在词缀块内匹配，物品名/说明里出现同一串不误杀。</summary>
    [Fact]
    public void Evaluate_ExcludeDoesNotMatchOutsideAffixBlocks()
    {
        var rules = new MapRuleSet
        {
            Affixes = [new MapAffixRule { Keyword = "无法回复", Kind = AffixKind.Exclude }],
        };

        var item = new ParsedItem
        {
            RawText = "稀 有 度: 稀有\n无法回复的绿洲\n放一张地图",
            Rarity = ItemRarity.Rare,
            AffixBlocks = ["+10% 物品数量"],
        };

        Assert.True(RuleEngine.Evaluate(item, rules).Passed);
    }

    // ═══ 必须包含词 ═══════════════════════════════════════

    [Fact]
    public void Evaluate_AnyRequire_OneHitPasses()
    {
        var rules = new MapRuleSet
        {
            IncludeMode = IncludeMode.AnyRequire,
            Affixes =
            [
                new MapAffixRule { Keyword = "额外怪物", Kind = AffixKind.Require },
                new MapAffixRule { Keyword = "制图师", Kind = AffixKind.Require },
            ],
        };

        Assert.True(RuleEngine.Evaluate(Rare("区域内含有额外怪物"), rules).Passed);
    }

    [Fact]
    public void Evaluate_AnyRequire_NoHitFails()
    {
        var rules = new MapRuleSet
        {
            IncludeMode = IncludeMode.AnyRequire,
            Affixes = [new MapAffixRule { Keyword = "额外怪物", Kind = AffixKind.Require }],
        };

        Assert.False(RuleEngine.Evaluate(Rare("+10% 物品数量"), rules).Passed);
    }

    [Fact]
    public void Evaluate_AllRequire_AllHitPasses()
    {
        var rules = new MapRuleSet
        {
            IncludeMode = IncludeMode.AllRequire,
            Affixes =
            [
                new MapAffixRule { Keyword = "物品数量", Kind = AffixKind.Require },
                new MapAffixRule { Keyword = "物品稀有度", Kind = AffixKind.Require },
            ],
        };

        Assert.True(RuleEngine.Evaluate(Rare("+10% 物品数量", "+5% 物品稀有度"), rules).Passed);
    }

    [Fact]
    public void Evaluate_AllRequire_PartialHitFails()
    {
        var rules = new MapRuleSet
        {
            IncludeMode = IncludeMode.AllRequire,
            Affixes =
            [
                new MapAffixRule { Keyword = "物品数量", Kind = AffixKind.Require },
                new MapAffixRule { Keyword = "物品稀有度", Kind = AffixKind.Require },
            ],
        };

        Assert.False(RuleEngine.Evaluate(Rare("+10% 物品数量"), rules).Passed);
    }

    [Fact]
    public void Evaluate_BlankKeyword_IsIgnored()
    {
        var rules = new MapRuleSet
        {
            Affixes = [new MapAffixRule { Keyword = "   ", Kind = AffixKind.Require }],
        };

        // 空白关键词不该变成「永远缺一条必需词缀」。
        Assert.True(RuleEngine.Evaluate(Rare("+10% 物品数量"), rules).Passed);
    }

    // ═══ 数值：All ════════════════════════════════════════

    [Fact]
    public void Evaluate_All_EveryThresholdMet_Passes()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.All,
            Thresholds = new MapStatThresholds { ItemQuantity = 25, ItemRarity = 15 },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 30,
            [MapStat.ItemRarity] = 20,
        });

        Assert.True(RuleEngine.Evaluate(item, rules).Passed);
    }

    [Fact]
    public void Evaluate_All_OneBelow_Fails()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.All,
            Thresholds = new MapStatThresholds { ItemQuantity = 25, ItemRarity = 15 },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 30,
            [MapStat.ItemRarity] = 10,
        });

        Assert.False(RuleEngine.Evaluate(item, rules).Passed);
    }

    /// <summary>缺失的词缀按「不达标」处理，而不是按 0 参与比较后悄悄放行。</summary>
    [Fact]
    public void Evaluate_All_MissingStat_Fails()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.All,
            Thresholds = new MapStatThresholds { ItemQuantity = 25 },
        };

        var item = RareWithStats(new() { [MapStat.MonsterPackSize] = 40 });

        Assert.False(RuleEngine.Evaluate(item, rules).Passed);
    }

    [Fact]
    public void Evaluate_All_ExactBoundary_Passes()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.All,
            Thresholds = new MapStatThresholds { ItemQuantity = 25 },
        };

        Assert.True(RuleEngine.Evaluate(
            RareWithStats(new() { [MapStat.ItemQuantity] = 25 }), rules).Passed);
    }

    // ═══ 数值：Any ════════════════════════════════════════

    [Fact]
    public void Evaluate_Any_OneMet_Passes()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.Any,
            Thresholds = new MapStatThresholds { ItemQuantity = 25, MonsterPackSize = 30 },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 10,
            [MapStat.MonsterPackSize] = 35,
        });

        Assert.True(RuleEngine.Evaluate(item, rules).Passed);
    }

    [Fact]
    public void Evaluate_Any_NoneMet_Fails()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.Any,
            Thresholds = new MapStatThresholds { ItemQuantity = 25, MonsterPackSize = 30 },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 10,
            [MapStat.MonsterPackSize] = 12,
        });

        Assert.False(RuleEngine.Evaluate(item, rules).Passed);
    }

    /// <summary>一条都不启用时默认通过（没有条件 = 不筛选），而不是否决。</summary>
    [Fact]
    public void Evaluate_Any_NoEnabledThresholds_Passes()
    {
        var rules = new MapRuleSet { MatchMode = MatchMode.Any, Thresholds = new() };
        Assert.True(RuleEngine.Evaluate(RareWithStats(new()), rules).Passed);
    }

    // ═══ 数值：Total ══════════════════════════════════════

    [Fact]
    public void Evaluate_Total_SumMeetsMin_Passes()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.Total,
            MatchTotalMin = 100,
            Thresholds = new MapStatThresholds
            {
                ItemQuantity = 20,
                ItemRarity = 20,
                MonsterPackSize = 20,
            },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 45,
            [MapStat.ItemRarity] = 30,
            [MapStat.MonsterPackSize] = 40,
        });

        Assert.True(RuleEngine.Evaluate(item, rules).Passed); // 45+30+40 = 115
    }

    [Fact]
    public void Evaluate_Total_SumBelowMin_Fails()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.Total,
            MatchTotalMin = 100,
            Thresholds = new MapStatThresholds { ItemQuantity = 20, ItemRarity = 20 },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 30,
            [MapStat.ItemRarity] = 20,
        });

        Assert.False(RuleEngine.Evaluate(item, rules).Passed); // 50 < 100
    }

    /// <summary>
    /// 求和模式下缺失项按 0 计入——这里折叠成 0 是<b>明确意图</b>（求和语义），
    /// 与 All/Any 模式下「缺失 = 不达标」是两回事。
    /// </summary>
    [Fact]
    public void Evaluate_Total_MissingStatCountsAsZero()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.Total,
            MatchTotalMin = 40,
            Thresholds = new MapStatThresholds { ItemQuantity = 20, MoreCurrency = 20 },
        };

        var item = RareWithStats(new() { [MapStat.ItemQuantity] = 45 });

        Assert.True(RuleEngine.Evaluate(item, rules).Passed); // 45 + 0
    }

    [Fact]
    public void Evaluate_Total_NegativeStatReducesSum()
    {
        var rules = new MapRuleSet
        {
            MatchMode = MatchMode.Total,
            MatchTotalMin = 30,
            Thresholds = new MapStatThresholds { ItemQuantity = 10, ItemRarity = 10 },
        };

        var item = RareWithStats(new()
        {
            [MapStat.ItemQuantity] = 45,
            [MapStat.ItemRarity] = -20,
        });

        Assert.False(RuleEngine.Evaluate(item, rules).Passed); // 25 < 30
    }

    // ═══ 正则逃生舱 ═══════════════════════════════════════

    [Fact]
    public void Evaluate_RegexMatch_Passes_AndShortCircuitsThresholds()
    {
        // 数值明明不达标，但正则命中就通过——这正是「正则完全接管」的语义。
        var rules = new MapRuleSet
        {
            MapRegex = "物品数量",
            MatchMode = MatchMode.All,
            Thresholds = new MapStatThresholds { ItemQuantity = 999 },
        };

        // 正则在 RawText 上匹配，所以这里必须让原文真的含该串（RareWithStats 的
        // RawText 是固定头部，不含词缀，不能拿它做正则用例）。
        var item = new ParsedItem
        {
            RawText = "稀 有 度: 稀有\n{ +1% 物品数量 }",
            Rarity = ItemRarity.Rare,
            AffixBlocks = ["+1% 物品数量"],
            Stats = new Dictionary<MapStat, int> { [MapStat.ItemQuantity] = 1 },
        };

        Assert.True(RuleEngine.Evaluate(item, rules).Passed);
    }

    [Fact]
    public void Evaluate_RegexNoMatch_Fails_EvenIfThresholdsMet()
    {
        var rules = new MapRuleSet
        {
            MapRegex = "^绝对不会匹配$",
            MatchMode = MatchMode.All,
            Thresholds = new MapStatThresholds { ItemQuantity = 1 },
        };

        // 原文含真实词缀，且数值远超门槛（1）；正则不命中仍须否决——
        // 这条与上一条互为反面，共同钉住「正则完全接管」。
        var item = new ParsedItem
        {
            RawText = "稀 有 度: 稀有\n{ +999% 物品数量 }",
            Rarity = ItemRarity.Rare,
            AffixBlocks = ["+999% 物品数量"],
            Stats = new Dictionary<MapStat, int> { [MapStat.ItemQuantity] = 999 },
        };

        Assert.False(RuleEngine.Evaluate(item, rules).Passed);
    }

    /// <summary>正则无效时按「规则不可用」否决，而不是默默放行全部物品。</summary>
    [Fact]
    public void Evaluate_InvalidRegex_Fails_AndSaysWhy()
    {
        var rules = new MapRuleSet { MapRegex = "([unclosed" };
        var verdict = RuleEngine.Evaluate(Rare("+10% 物品数量"), rules);

        Assert.False(verdict.Passed);
        Assert.Contains("正则无效", verdict.Reason);
    }

    [Fact]
    public void Evaluate_WhitespaceRegex_IsTreatedAsDisabled()
    {
        var rules = new MapRuleSet { MapRegex = "   " };
        Assert.True(RuleEngine.Evaluate(Rare("+10% 物品数量"), rules).Passed);
    }

    // ═══ 判定原因 ═════════════════════════════════════════

    [Fact]
    public void Evaluate_FailReason_IsChinese_AndNamesTheKeyword()
    {
        var rules = new MapRuleSet
        {
            Affixes = [new MapAffixRule { Keyword = "无法回复", Kind = AffixKind.Exclude }],
        };

        var verdict = RuleEngine.Evaluate(Rare("玩家无法回复生命值"), rules);

        Assert.False(verdict.Passed);
        Assert.Contains("无法回复", verdict.Reason);
    }

    [Fact]
    public void Evaluate_MissingRequiredReason_ListsKeywords()
    {
        var rules = new MapRuleSet
        {
            IncludeMode = IncludeMode.AllRequire,
            Affixes = [new MapAffixRule { Keyword = "额外怪物", Kind = AffixKind.Require }],
        };

        var verdict = RuleEngine.Evaluate(Rare("+10% 物品数量"), rules);

        Assert.Contains("额外怪物", verdict.Reason);
    }

    // ═══ 门槛索引器 ═══════════════════════════════════════

    [Fact]
    public void Thresholds_Enabled_EnumeratesOnlyNonNull()
    {
        var t = new MapStatThresholds { ItemQuantity = 20, MoreCurrency = 0 };
        var enabled = t.Enabled().ToList();

        Assert.Equal(2, enabled.Count);
        // 显式写 0 是「启用且门槛为 0」，必须出现在启用集合里。
        Assert.Contains((MapStat.MoreCurrency, 0), enabled);
        Assert.DoesNotContain(enabled, e => e.Stat == MapStat.ItemRarity);
    }
}
