using System.Text;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// CsdDocument 的安全底座测试：round-trip 是所有写操作的前提，
/// 分层共栖（外来标签保留）是与实例补丁/其他补丁共存的关键行为。
/// 夹具取自真实地图词缀描述文件（简中/繁中/默认语言混合，UTF-16LE BOM + CRLF）。
/// </summary>
public sealed class CsdDocumentTests
{
    private static byte[] FixtureBytes() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "csd-sample.csd"));

    private static string TextOf(byte[] bytes)
    {
        var doc = CsdDocument.Parse(bytes);
        var raw = doc.Serialize();
        return Encoding.Unicode.GetString(raw, 2, raw.Length - 2); // 跳过 BOM
    }

    private static byte[] Doc(params string[] lines)
    {
        var text = string.Join("\r\n", lines) + "\r\n";
        var payload = Encoding.Unicode.GetBytes(text);
        var result = new byte[payload.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        payload.CopyTo(result, 2);
        return result;
    }

    [Fact]
    public void ParseSerialize_RoundTrip_ByteIdentical()
    {
        var bytes = FixtureBytes();
        var doc = CsdDocument.Parse(bytes);
        Assert.Equal(bytes, doc.Serialize());
    }

    [Fact]
    public void Parse_EnumeratesStatsWithTargetLanguageLines()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        Assert.Contains(doc.Stats, s => s.Key == "map_tempest_display_prefix map_tempest_display_suffix");
        var rarity = doc.Stats.SingleOrDefault(s => s.Key == "map_item_drop_rarity_+%");
        Assert.NotNull(rarity);
        Assert.Equal(2, rarity!.TargetLanguageLineIndexes.Count);
    }

    [Fact]
    public void GetDisplayText_ResolvesColorAndBracketMarkup()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        Assert.Equal(
            "该区域内物品稀有度提高 {0}%该区域内物品稀有度降低 {0}%",
            doc.GetDisplayText("map_item_drop_rarity_+%"));
    }

    /// <summary>一条 stat 的多行文本要能按行拆开：游戏按实际数值只显示其中一行，
    /// 列表拼成一段会失真（"提高 X%降低 X%"连读）。</summary>
    [Fact]
    public void GetDisplayLines_SplitsMultiLineStat()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        var lines = doc.GetDisplayLines("map_item_drop_rarity_+%");
        Assert.Equal(2, lines.Count);
        Assert.Equal("该区域内物品稀有度提高 {0}%", lines[0]);
        Assert.Equal("该区域内物品稀有度降低 {0}%", lines[1]);

        // 只有占位符的模板型词缀：一段文本，游戏运行时才把前后缀填进 {0} {1}
        Assert.Equal(["{0} {1}"], doc.GetDisplayLines("map_tempest_display_prefix map_tempest_display_suffix"));

        // 预览行与拼接文本的对应关系
        Assert.Equal(string.Concat(lines), doc.GetDisplayText("map_item_drop_rarity_+%"));
    }

    [Fact]
    public void ApplyColor_WrapsOnlyResolvedLanguageSection()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        Assert.True(doc.TryApplyColor("map_item_drop_rarity_+%", "LuckyPurple"));

        var text = TextOf(doc.Serialize());
        // 未指定客户端语言时按 简中 > 繁中 > 英文 取第一段有的（这里是简中），且只打这一段
        Assert.Contains("\"<LuckyPurple>{{该区域内物品[Rarity|稀有度]提高 {0}%}}\"", text);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(text, "<LuckyPurple>")); // 简中段 2 行
        // 繁中段、默认段（英文）与其他语言一律不动
        Assert.Contains("\"增加{0}%此區域找到的物品[Rarity|稀有度]\"", text);
        Assert.Contains("\"{0}% increased [Rarity] of Items found in this Area\"", text);
        Assert.Contains("\"[Rarity|Raridade] de itens encontrados nesta área aumentada em {0}%\"", text);
    }

    /// <summary>简体中文客户端：只给简中段上色。</summary>
    [Fact]
    public void ApplyColor_SimplifiedChineseClient_ColorsSimplifiedSectionOnly()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        Assert.True(doc.TryApplyColor("map_item_drop_rarity_+%", "LuckyPurple", CsdDocument.TargetLanguage));

        var text = TextOf(doc.Serialize());
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(text, "<LuckyPurple>"));
        Assert.Contains("\"<LuckyPurple>{{该区域内物品[Rarity|稀有度]提高 {0}%}}\"", text);
        Assert.Contains("\"{0}% increased [Rarity] of Items found in this Area\"", text);
    }

    /// <summary>繁体中文客户端：只给繁中段上色，简中段不动。</summary>
    [Fact]
    public void ApplyColor_TraditionalChineseClient_ColorsTraditionalSectionOnly()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        Assert.True(doc.TryApplyColor("map_item_drop_rarity_+%", "LuckyPurple", "Traditional Chinese"));

        var text = TextOf(doc.Serialize());
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(text, "<LuckyPurple>"));
        Assert.Contains("\"<LuckyPurple>{{增加{0}%此區域找到的物品[Rarity|稀有度]}}\"", text);
        Assert.Contains("\"该区域内物品[Rarity|稀有度]提高 {0}%\"", text);
    }

    /// <summary>指定了客户端语言但该词缀没有这一段时，按 简中 > 繁中 > 英文 退回。</summary>
    [Fact]
    public void ApplyColor_ClientLanguageMissing_FallsBackToResolvedOrder()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 zh_only_stat",
            "\t1",
            "\t\t1 \"English text\"",
            "\tlang \"Simplified Chinese\"",
            "\t1",
            "\t\t1 \"简体中文\""));

        // 繁体中文客户端，但该词缀没有繁中段 → 退回简中段
        Assert.True(doc.TryApplyColor("zh_only_stat", "LuckyPurple", "Traditional Chinese"));

        var text = TextOf(doc.Serialize());
        Assert.Contains("\"<LuckyPurple>{{简体中文}}\"", text);
        Assert.Contains("\"English text\"", text);
    }

    [Fact]
    public void ApplyColor_Twice_IsIdempotent()
    {
        var doc = CsdDocument.Parse(FixtureBytes());
        Assert.True(doc.TryApplyColor("map_item_drop_rarity_+%", "LuckyPurple"));
        var once = doc.Serialize();

        Assert.False(doc.TryApplyColor("map_item_drop_rarity_+%", "LuckyPurple"));
        Assert.Equal(once, doc.Serialize());
    }

    [Fact]
    public void ApplyColor_TempestStat_WrapsSingleResolvedLanguage()
    {
        var doc = CsdDocument.Parse(FixtureBytes());
        var key = "map_tempest_display_prefix map_tempest_display_suffix";

        Assert.True(doc.TryApplyColor(key, "LuckyPurple"));

        var text = TextOf(doc.Serialize());
        // 只打解析出的那一段（简中）；繁中/英文/葡/俄/泰/德/西/法/韩/日原样
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(text, "<LuckyPurple>"));
        Assert.Contains("\"<LuckyPurple>{{{0} {1}}}\" tempest_mod_text 1 tempest_mod_text 2", text);
        Assert.Contains("\"{0}{1}\" tempest_mod_text 1 tempest_mod_text 2", text); // 德语段原样
    }

    /// <summary>语言回退：简中缺失用繁中，繁中也缺才用英文（PRD：优先简中 &gt; 繁中 &gt; 英文）。</summary>
    [Fact]
    public void LanguageFallback_SimplifiedThenTraditionalThenEnglish()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 zh_stat",
            "\t1",
            "\t\t1 \"English text\"",
            "\tlang \"Traditional Chinese\"",
            "\t1",
            "\t\t1 \"繁體中文\"",
            "\tlang \"Simplified Chinese\"",
            "\t1",
            "\t\t1 \"简体中文\"",
            "description",
            "\t1 zh_missing_stat",
            "\t1",
            "\t\t1 \"English text\"",
            "\tlang \"Traditional Chinese\"",
            "\t1",
            "\t\t1 \"只有繁中\"",
            "description",
            "\t1 en_only_stat",
            "\t1",
            "\t\t1 \"English only\""));

        Assert.Equal("简体中文", doc.GetDisplayText("zh_stat"));
        Assert.Equal(CsdDocument.TargetLanguage, doc.ResolveLanguage("zh_stat"));

        Assert.Equal("只有繁中", doc.GetDisplayText("zh_missing_stat"));
        Assert.Equal("Traditional Chinese", doc.ResolveLanguage("zh_missing_stat"));

        Assert.Equal("English only", doc.GetDisplayText("en_only_stat"));
        Assert.Equal(CsdDocument.DefaultLanguage, doc.ResolveLanguage("en_only_stat"));

        // 条目登记的语言按优先级排序，只含被跟踪的三种
        Assert.Equal(["Simplified Chinese", "Traditional Chinese", "English"],
            doc.Stats.Single(s => s.Key == "zh_stat").Languages());
        Assert.Equal(["Traditional Chinese", "English"],
            doc.Stats.Single(s => s.Key == "zh_missing_stat").Languages());
    }

    /// <summary>语言精简包（csd 只剩默认段、内容仍是中文）：照样能读、能上色。</summary>
    [Fact]
    public void SingleLanguageDocument_ReadsAndColorsDefaultSection()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 some_stat",
            "\t1",
            "\t\t1 \"精简客户端的文本\""));

        var stat = Assert.Single(doc.Stats);
        Assert.Empty(stat.TargetLanguageLineIndexes);
        Assert.Equal("精简客户端的文本", doc.GetDisplayText("some_stat"));
        Assert.Equal(CsdDocument.DefaultLanguage, doc.ResolveLanguage("some_stat"));

        Assert.True(doc.TryApplyColor("some_stat", "LuckyPurple"));
        Assert.Contains("\"<LuckyPurple>{{精简客户端的文本}}\"", TextOf(doc.Serialize()));
    }

    [Fact]
    public void ApplyColor_InvalidColorId_Throws()
    {
        var doc = CsdDocument.Parse(FixtureBytes());
        Assert.Throws<ArgumentException>(() => doc.TryApplyColor("map_item_drop_rarity_+%", "1bad"));
        Assert.Throws<ArgumentException>(() => doc.TryApplyColor("map_item_drop_rarity_+%", "含中文"));
    }

    [Fact]
    public void StripColors_RemovesOwnedTags_KeepsForeignTags()
    {
        var bytes = Doc(
            "description",
            "\t1 owned_stat",
            "\t1",
            "\t\t1 \"英文\"",
            "\tlang \"Simplified Chinese\"",
            "\t1",
            "\t\t1 \"净收益提高\"",
            "\t1 foreign_stat",
            "\t1",
            "\tlang \"Simplified Chinese\"",
            "\t1",
            "\t\t1 \"<efarmLucky>{{保留}}</efarmLucky>\"");
        var doc = CsdDocument.Parse(bytes);

        Assert.True(doc.TryApplyColor("owned_stat", "LuckyPurple"));
        var stripped = doc.StripColors(["LuckyPurple"]);
        Assert.Equal(1, stripped); // 只打了简中段一处

        var text = TextOf(doc.Serialize());
        Assert.Contains("\"净收益提高\"", text);
        Assert.Contains("\"英文\"", text);
        Assert.DoesNotContain("<LuckyPurple>", text);
        Assert.Contains("<efarmLucky>{{保留}}</efarmLucky>", text);
    }

    [Fact]
    public void ApplyColor_ThenStrip_RestoresOriginalText()
    {
        var original = FixtureBytes();
        var doc = CsdDocument.Parse(original);
        doc.TryApplyColor("map_item_drop_rarity_+%", "DangerRed");
        doc.StripColors(["DangerRed"]);

        Assert.Equal(original, doc.Serialize());
    }

    [Fact]
    public void Utf8NoBom_RoundTrip_Preserved()
    {
        var text = "description\r\n\t1 some_stat\r\n\t1\r\n\t\t1 \"text\"\r\n";
        var bytes = new UTF8Encoding(false).GetBytes(text);

        var doc = CsdDocument.Parse(bytes);
        Assert.Equal(bytes, doc.Serialize());
    }

    /// <summary>外部工具重写过的游戏文件可能丢掉 BOM：内容仍是 UTF-16LE，必须照常解析且序列化不加回 BOM。</summary>
    [Fact]
    public void Utf16NoBom_ParsesTargetLanguage_AndKeepsNoBom()
    {
        var text = string.Join("\r\n",
            "description",
            "\t1 some_stat",
            "\t1",
            "\t\t1 \"Plain English text\"",
            "\tlang \"Simplified Chinese\"",
            "\t1",
            "\t\t1 \"净收益提高\"") + "\r\n";
        var bytes = Encoding.Unicode.GetBytes(text); // 无 BOM

        var doc = CsdDocument.Parse(bytes);
        Assert.Equal(bytes, doc.Serialize()); // 解析→序列化逐字节相等，且不擅自加 BOM

        var stat = Assert.Single(doc.Stats);
        Assert.Single(stat.TargetLanguageLineIndexes);
        Assert.Equal("净收益提高", doc.GetDisplayText("some_stat"));

        Assert.True(doc.TryApplyColor("some_stat", "LuckyPurple"));
        var modified = doc.Serialize();
        Assert.False(modified.Length >= 2 && modified[0] == 0xFF && modified[1] == 0xFE);
        Assert.Contains("<LuckyPurple>{{净收益提高}}", Encoding.Unicode.GetString(modified));
    }

    /// <summary>组合型词缀（文本只有占位符、内容由游戏运行时填充）能被识别，用于列表标注。</summary>
    [Fact]
    public void IsPlaceholderOnly_DetectsRuntimeComposedStats()
    {
        Assert.True(CsdMarkup.IsPlaceholderOnly("{0} {1}"));
        Assert.True(CsdMarkup.IsPlaceholderOnly("{0}%"));
        Assert.False(CsdMarkup.IsPlaceholderOnly("该区域内物品数量提高 {0}%"));
        Assert.False(CsdMarkup.IsPlaceholderOnly("Has no Sockets"));
        Assert.False(CsdMarkup.IsPlaceholderOnly(""));
    }

    /// <summary>数值染色：只给行里的数值占位符包标签，词缀名保持默认色（与"数值分档"补丁的做法一致）。</summary>
    [Fact]
    public void ApplyColorToValue_WrapsOnlyPlaceholder()
    {
        var doc = CsdDocument.Parse(FixtureBytes());

        Assert.True(doc.TryApplyColorToValue("map_item_drop_rarity_+%", "LuckyPurple"));

        var text = TextOf(doc.Serialize());
        Assert.Contains("1|# \"该区域内物品[Rarity|稀有度]提高 <LuckyPurple>{{{0}%}}\"", text);
        Assert.Contains("\"{0}% increased [Rarity] of Items found in this Area\"", text); // 英文段原样（不是客户端语言）
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(text, "<LuckyPurple>")); // 简中段 2 行，各只染数值
    }

    /// <summary>按区间分档：数值区间越高的行用越靠前（越亮）的颜色，且只染数值。</summary>
    [Fact]
    public void ApplyColorRamp_UsesHigherTierColorForHigherRange()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t1|124 \"{0:+d} 低档\"",
            "\t\t125|149 \"{0:+d} 中档\"",
            "\t\t150|# \"{0:+d} 高档\""));

        Assert.True(doc.TryApplyColorRamp("stat_a", ["C1", "C2", "C3"]));

        var text = TextOf(doc.Serialize());
        Assert.Contains("\"<C3>{{{0:+d}}} 低档\"", text); // 下限 1 → 最低档
        Assert.Contains("\"<C2>{{{0:+d}}} 中档\"", text); // 下限 125
        Assert.Contains("\"<C1>{{{0:+d}}} 高档\"", text); // 下限 150 → 最高档
    }

    /// <summary>剥离要同时认得带闭合（本工具生成）与无闭合（外部补丁常用）两种写法。</summary>
    [Fact]
    public void StripColors_RemovesBothClosedAndOpenForms()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t1 \"<Old1>{{{0:+d}}}\"",
            "\t\t1 \"<Old2>{{{0:+d}}}</Old2>\""));

        Assert.Equal(2, doc.StripColors(["Old1", "Old2"]));

        var text = TextOf(doc.Serialize());
        Assert.DoesNotContain("<Old1>", text);
        Assert.DoesNotContain("<Old2>", text);
        Assert.Contains("\"{0:+d}\"", text);
    }

    /// <summary>方向拆行：无区间但带正负号的单行，拆成 <c>1|#</c>（正）与 <c>#|-1 … negate 1</c>（负）两行，各自染色值。</summary>
    [Fact]
    public void ApplyColorRamp_SplitsSignedLineIntoTwoDirections()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t# \"技能等级 {0:+d}\""));

        Assert.True(doc.TryApplyColorRamp("stat_a", ["AT1", "AT2"], ["DA1"]));

        var text = TextOf(doc.Serialize());
        Assert.Contains("1|# \"技能等级 <AT1>{{{0:+d}}}\"", text);
        Assert.Contains("#|-1 \"技能等级 <DA1>{{{0:+d}}}\" negate 1", text);
    }

    /// <summary>已经有区间的行不再拆分（文件本身已给出方向或档位）。</summary>
    [Fact]
    public void ApplyColorRamp_DoesNotSplitLinesThatAlreadyHaveRange()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t1|# \"提高 {0:+d}\"",
            "\t\t#|-1 \"降低 {0:+d}\" negate 1"));

        Assert.True(doc.TryApplyColorRamp("stat_a", ["AT1"], ["DA1"]));

        var text = TextOf(doc.Serialize());
        Assert.DoesNotContain("1|# 1|#", text); // 没有二次拆分
        Assert.Contains("1|# \"提高 <AT1>{{{0:+d}}}\"", text);
        Assert.Contains("#|-1 \"降低 <DA1>{{{0:+d}}}\" negate 1", text);
    }

    /// <summary>原版自带方向拆行的词缀（如「攻击技能的闪电伤害提高」<c>1|#</c> + <c>#|-1</c>）
    /// 也必须按真实档位分档：否则会掉进按正负染色的兜底路径，低档也显示最亮色。</summary>
    [Fact]
    public void ApplyColorRamp_SplitsByTier_OnOriginalDirectionalRows()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t2",
            "\t\t1|# \"提高 {0}%\"",
            "\t\t#|-1 \"降低 {0}%\" negate 1"));

        Assert.True(doc.TrySplitByTier("stat_a", [(1, 4, 3, 3), (5, 7, 2, 3), (8, 12, 1, 3)], ["T1", "T2", "T3", "T4", "T5"]));

        var text = TextOf(doc.Serialize());
        // 正向：数值从高到低对应 T1→T3
        Assert.Contains("1|4 \"提高 <T3>{{{0}%}}\"", text);
        Assert.Contains("5|7 \"提高 <T2>{{{0}%}}\"", text);
        Assert.Contains("8|# \"提高 <T1>{{{0}%}}\"", text);
        // 负向：绝对值从大到小镜像分档（-8/-12 与 +8/+12 同为 T1）
        Assert.Contains("#|-8 \"降低 <T1>{{{0}%}}\" negate 1", text);
        Assert.Contains("-7|-5 \"降低 <T2>{{{0}%}}\" negate 1", text);
        Assert.Contains("-4|-1 \"降低 <T3>{{{0}%}}\" negate 1", text);
        Assert.Contains("\r\n\t6\r\n", text); // 段数量行 2 → 6（正向 3 + 负向 3）
    }

    /// <summary>拆行后行号同步正确：所有标签都能被完整剥离（不变量：插入行后 stat 行号仍指向正确位置）。</summary>
    [Fact]
    public void SplitDirectional_KeepsLineIndexesValid()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 first_stat",
            "\t1",
            "\t\t# \"第一个 {0:+d}\"",
            "description",
            "\t1 second_stat",
            "\t1",
            "\t\t# \"第二个 {0:+d}\""));

        Assert.True(doc.TryApplyColorRamp("first_stat", ["AT1"], ["DA1"]));
        Assert.True(doc.TryApplyColorRamp("second_stat", ["AT1"], ["DA1"]));

        var text = TextOf(doc.Serialize());
        Assert.Contains("第一个 <AT1>{{{0:+d}}}", text);
        Assert.Contains("第二个 <AT1>{{{0:+d}}}", text); // 第二个 stat 的行号在插入后仍正确
        Assert.Equal(4, doc.StripColors(["AT1", "DA1"]));
    }

    /// <summary>按 tier 阶梯拆行：只染最高的 5 档（T1~T5），更低的档合并成兜底行且不上色。</summary>
    [Fact]
    public void ApplyColorRamp_SplitsByTierAndColorsTopFive()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t# \"上限 {0:+d}\""));

        var tiers = new List<(int Min, int Max, int Tier, int LineTiers)>
        {
            (10, 24, 8, 8), (25, 49, 7, 8), (50, 74, 6, 8), (75, 99, 5, 8),
            (100, 124, 4, 8), (125, 149, 3, 8), (150, 174, 2, 8), (175, 199, 1, 8),
        };
        Assert.True(doc.TrySplitByTier("stat_a", tiers, ["C1", "C2", "C3", "C4", "C5"]));

        var text = TextOf(doc.Serialize());
        Assert.Contains("1|9 \"上限 {0:+d}\"", text);                              // 低于最低档：兜底、不上色
        Assert.Contains("10|74 \"上限 {0:+d}\"", text);                            // T8~T6：超出染色窗口，独立段不上色
        Assert.Contains("75|99 \"上限 <C5>{{{0:+d}}}\"", text);                // T5：最暗
        Assert.Contains("100|124 \"上限 <C4>{{{0:+d}}}\"", text);             // T4
        Assert.Contains("125|149 \"上限 <C3>{{{0:+d}}}\"", text);             // T3
        Assert.Contains("150|174 \"上限 <C2>{{{0:+d}}}\"", text);             // T2
        Assert.Contains("175|# \"上限 <C1>{{{0:+d}}}\"", text);               // T1：最亮，最高档用 |# 兜住超出值
        Assert.Contains("\r\n\t7\r\n", text);                                      // 段数量行同步：1 行拆成 7 行后必须写 7
    }

    /// <summary>档数不足 5 档时按实际档数染色（有几档染几档），低于最高档的合并成兜底。</summary>
    [Fact]
    public void ApplyColorRamp_SplitsByTierWithFewerTiersThanColors()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t# \"上限 {0:+d}\""));

        Assert.True(doc.TrySplitByTier("stat_a", [(20, 25, 2, 2), (26, 32, 1, 2)], ["C1", "C2", "C3", "C4", "C5"]));

        var text = TextOf(doc.Serialize());
        Assert.Contains("1|19 \"上限 {0:+d}\"", text);
        Assert.Contains("20|25 \"上限 <C2>{{{0:+d}}}\"", text);           // 次高档：色阶第二个
        Assert.Contains("26|# \"上限 <C1>{{{0:+d}}}\"", text);           // 最高档永远是 C1（最亮）
        Assert.DoesNotContain("<C3>", text);                                 // 只用了前两个颜色
    }

    /// <summary>单点区间（min==max）合法（游戏原版 csd 自己在用，如 3|3）；负数区间必须被过滤。</summary>
    [Fact]
    public void ApplyColorRamp_SplitsByTier_SupportsSinglePointRangesAndFiltersNegative()
    {
        var doc = CsdDocument.Parse(Doc(
            "description",
            "\t1 stat_a",
            "\t1",
            "\t\t# \"上限 {0:+d}\""));

        var tiers = new List<(int Min, int Max, int Tier, int LineTiers)>
        {
            (1, 1, 3, 3), (2, 2, 2, 3), (3, 3, 1, 3),
        };
        Assert.True(doc.TrySplitByTier("stat_a", tiers, ["C1", "C2", "C3", "C4", "C5"]));

        var text = TextOf(doc.Serialize());
        Assert.DoesNotContain("-5|", text);
        Assert.Contains("1|1 \"上限 <C3>{{{0:+d}}}\"", text);   // 最低档 T3
        Assert.Contains("2|2 \"上限 <C2>{{{0:+d}}}\"", text);   // T2
        Assert.Contains("3|# \"上限 <C1>{{{0:+d}}}\"", text);   // T1：最高档用 |# 兜住
        Assert.DoesNotContain("\t\t# \"上限", text);            // 原行已被替换
    }

    /// <summary>无闭合标签包裹数值占位符（{{{0:+d}}} 三连花括号）时，解析出的分段必须保留完整占位符
    /// （含右括号）——此前懒惰匹配会把内容解析成 {0:+d、把最后一个 } 落到普通文本里。</summary>
    [Fact]
    public void CsdMarkup_OpenTagAroundPlaceholder_KeepsClosingBrace()
    {
        var segments = CsdMarkup.Parse("<C3>{{{0:+d}}} 命中值");

        Assert.Equal(2, segments.Count);
        Assert.Equal("C3", segments[0].ColorId);
        Assert.Equal("{0:+d}", segments[0].Text);
        Assert.Null(segments[1].ColorId);
        Assert.Equal(" 命中值", segments[1].Text);
    }

    [Fact]
    public void CsdMarkup_BracketTags_ResolvedToDisplayText()    {
        var segments = CsdMarkup.Parse("[MapBoss|地图首领]掉落的物品数量提高 {0}%");

        var segment = Assert.Single(segments);
        Assert.Null(segment.ColorId);
        Assert.Equal("地图首领掉落的物品数量提高 {0}%", segment.Text);
    }

    [Fact]
    public void CsdMarkup_ColorTags_ProduceColoredSegments()
    {
        var segments = CsdMarkup.Parse("前缀<VeryLucky>{{[A|甲]文本}}</VeryLucky>后缀");

        Assert.Equal(3, segments.Count);
        Assert.Equal("前缀", segments[0].Text);
        Assert.Null(segments[0].ColorId);
        Assert.Equal("甲文本", segments[1].Text);
        Assert.Equal("VeryLucky", segments[1].ColorId);
        Assert.Equal("后缀", segments[2].Text);
    }
}
