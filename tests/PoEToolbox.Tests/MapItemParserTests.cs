using PoEToolbox.Plugins.MapWorkbench.Core;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 物品文本解析（SPEC §6 的第一交付物）。
///
/// <para>
/// 样本来自 <c>Fixtures/map-items/</c>。⚠ 这些样本目前是<b>按已知的客户端文本格式手工构造的</b>，
/// 尚未用真机 <c>Ctrl+C</c> 取样核对（SPEC §7 的阻塞项①）。真机样本到位后，
/// 应把本文件的样本换成实物，并保留这里每一条断言——断言表达的是<b>语义要求</b>，
/// 与样本来源无关；若实物与断言冲突，先怀疑断言背后的假设（尤其
/// <see cref="Stats_UsesAffixBlock_OverPropertyLine"/> 那条优先级）。
/// </para>
/// <para>
/// 本类不碰任何进程级静态（不开配置、不写日志），因此不需要挂 <c>ConfigPathTestCollection</c>。
/// </para>
/// </summary>
public sealed class MapItemParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "map-items", name));

    // ═══ 稀有度与状态位 ═══════════════════════════════════

    [Theory]
    [InlineData("rare-6affix-simplified.txt", ItemRarity.Rare)]
    [InlineData("rare-8affix-corrupted.txt", ItemRarity.Rare)]
    [InlineData("rare-unidentified.txt", ItemRarity.Rare)]
    [InlineData("rare-english.txt", ItemRarity.Rare)]
    [InlineData("magic-map.txt", ItemRarity.Magic)]
    [InlineData("normal-map-quality0.txt", ItemRarity.Normal)]
    public void Parse_DetectsRarity(string file, ItemRarity expected)
        => Assert.Equal(expected, MapItemParser.Parse(Fixture(file)).Rarity);

    [Fact]
    public void Parse_UnidentifiedItem_IsNotIdentified()
        => Assert.False(MapItemParser.Parse(Fixture("rare-unidentified.txt")).IsIdentified);

    [Fact]
    public void Parse_IdentifiedItem_IsIdentified()
        => Assert.True(MapItemParser.Parse(Fixture("rare-6affix-simplified.txt")).IsIdentified);

    [Fact]
    public void Parse_CorruptedItem_FlagSet()
        => Assert.True(MapItemParser.Parse(Fixture("rare-8affix-corrupted.txt")).IsCorrupted);

    [Fact]
    public void Parse_EnglishClient_AlsoWorks()
    {
        var item = MapItemParser.Parse(Fixture("rare-english.txt"));
        Assert.Equal(ItemRarity.Rare, item.Rarity);
        Assert.True(item.IsMap);
        Assert.True(item.IsCorrupted);
    }

    [Fact]
    public void Parse_NormalMap_IsNotPolishableRare()
        => Assert.False(MapItemParser.Parse(Fixture("normal-map-quality0.txt")).Rarity == ItemRarity.Rare);

    // ═══ 词缀分块与计数 ═══════════════════════════════════

    [Fact]
    public void AffixCount_SixAffixMap_IsSix()
        => Assert.Equal(6, MapItemParser.Parse(Fixture("rare-6affix-simplified.txt")).AffixCount);

    [Fact]
    public void AffixCount_EightAffixMap_IsEight()
        => Assert.Equal(8, MapItemParser.Parse(Fixture("rare-8affix-corrupted.txt")).AffixCount);

    [Fact]
    public void SplitAffixBlocks_UnclosedBrace_IsDropped()
    {
        // 未闭合的花括号不能凭空造出一条词缀——否则「6 词缀」判定会误通过。
        var blocks = MapItemParser.SplitAffixBlocks("{ +10% 物品数量 } { 未闭合");
        Assert.Single(blocks);
        Assert.Equal("+10% 物品数量", blocks[0]);
    }

    [Fact]
    public void SplitAffixBlocks_EmptyBraces_AreIgnored()
    {
        var blocks = MapItemParser.SplitAffixBlocks("{}{   }{ +1% 物品数量 }");
        Assert.Single(blocks);
    }

    [Fact]
    public void AffixCount_NoBraces_IsZero()
        => Assert.Equal(0, MapItemParser.Parse("稀 有 度: 普通\n荒芜的绿洲\n").AffixCount);

    // ═══ 数值提取 ═════════════════════════════════════════

    [Fact]
    public void Stats_SixAffixMap_ExtractsAllThree()
    {
        var item = MapItemParser.Parse(Fixture("rare-6affix-simplified.txt"));

        Assert.Equal(28, item.GetStatOr(MapStat.ItemQuantity, -1));
        Assert.Equal(18, item.GetStatOr(MapStat.ItemRarity, -1));
        Assert.Equal(35, item.GetStatOr(MapStat.MonsterPackSize, -1));
    }

    [Fact]
    public void Stats_EightAffixMap_ExtractsMoreStats()
    {
        var item = MapItemParser.Parse(Fixture("rare-8affix-corrupted.txt"));

        Assert.Equal(45, item.GetStatOr(MapStat.ItemQuantity, -1));
        Assert.Equal(12, item.GetStatOr(MapStat.MoreMaps, -1));
        Assert.Equal(8, item.GetStatOr(MapStat.MoreScarabs, -1));
        Assert.Equal(15, item.GetStatOr(MapStat.MoreCurrency, -1));
    }

    /// <summary>
    /// 负数必须读出负号。这是外部工具会挂的用例：它用 <c>IndexOf("+")</c> 定位数值，
    /// 遇到 <c>-10%</c> 会往前找到别处的 <c>+</c>，读出别的词缀的数字。
    /// </summary>
    [Fact]
    public void TryExtractFirstNumber_NegativeValue_KeepsSign()
    {
        Assert.True(MapItemParser.TryExtractFirstNumber("玩家的抗性上限 -10%", out int v));
        Assert.Equal(-10, v);
    }

    [Fact]
    public void TryExtractFirstNumber_PositiveValue()
    {
        Assert.True(MapItemParser.TryExtractFirstNumber("+35% 怪物群大小", out int v));
        Assert.Equal(35, v);
    }

    [Fact]
    public void TryExtractFirstNumber_NoPercentSign_StillReads()
    {
        // 「地图阶级: 16」这类没有百分号，同样要能取数。
        Assert.True(MapItemParser.TryExtractFirstNumber("地图阶级: 16", out int v));
        Assert.Equal(16, v);
    }

    [Fact]
    public void TryExtractFirstNumber_NoDigits_ReturnsFalse()
    {
        Assert.False(MapItemParser.TryExtractFirstNumber("区域内含有额外怪物", out _));
    }

    [Fact]
    public void TryExtractFirstNumber_Decimal_RoundsAwayFromZero()
    {
        Assert.True(MapItemParser.TryExtractFirstNumber("+2.5% 物品数量", out int v));
        Assert.Equal(3, v);
    }

    /// <summary>
    /// 逐块匹配的核心保证：一个词缀块只贡献它自己那条词缀的数值。
    /// 「物品数量」与「物品稀有度」都以「物品」开头，整段 Contains 会互相误命中。
    /// </summary>
    [Fact]
    public void Stats_AdjacentKeywordAffixes_DoNotCrossContaminate()
    {
        var item = MapItemParser.Parse(
            "稀 有 度: 稀有\n{ +35% 物品数量 }\n{ +18% 物品稀有度 }\n");

        Assert.True(item.TryGetStat(MapStat.ItemQuantity, out int qty));
        Assert.True(item.TryGetStat(MapStat.ItemRarity, out int rar));
        Assert.Equal(35, qty);
        Assert.Equal(18, rar);
    }

    /// <summary>
    /// 「键不存在」必须区别于「值为 0」——外部工具把两者混为一谈，
    /// 导致「词缀没匹配上」和「词缀真是 0%」在界面上无法分辨。
    /// </summary>
    [Fact]
    public void Stats_MissingKeyword_IsAbsent_NotZero()
    {
        var item = MapItemParser.Parse("稀 有 度: 稀有\n{ 区域内含有额外怪物 }\n");

        Assert.False(item.TryGetStat(MapStat.ItemQuantity, out _));
        Assert.DoesNotContain(MapStat.ItemQuantity, item.Stats.Keys);
    }

    /// <summary>真的解析到 0 时，键必须存在且值为 0（与「缺失」形成对照）。</summary>
    [Fact]
    public void Stats_ExplicitZero_IsPresentAsZero()
    {
        var item = MapItemParser.Parse("稀 有 度: 稀有\n{ +0% 物品数量 }\n");

        Assert.True(item.TryGetStat(MapStat.ItemQuantity, out int v));
        Assert.Equal(0, v);
    }

    // ═══ 阶级 / 品质 / 物品等级 ═══════════════════════════

    [Fact]
    public void Parse_MapTier_And_ItemLevel()
    {
        var item = MapItemParser.Parse(Fixture("rare-6affix-simplified.txt"));

        Assert.Equal(16, item.MapTier);
        Assert.Equal(83, item.ItemLevel);
    }

    [Fact]
    public void Parse_QualityZero_IsZeroNotNull()
    {
        // 「品质: +0%」是 0，不是「没有品质行」——制图钉要不要刷取决于这个区别。
        var item = MapItemParser.Parse(Fixture("normal-map-quality0.txt"));
        Assert.Equal(0, item.Quality);
    }

    [Fact]
    public void Parse_NoQualityLine_IsNull()
        => Assert.Null(MapItemParser.Parse(Fixture("rare-unidentified.txt")).Quality);

    // ═══ 空白与畸形输入 ═══════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void Parse_BlankText_IsSafe(string text)
    {
        var item = MapItemParser.Parse(text);

        Assert.Equal(ItemRarity.Unknown, item.Rarity);
        Assert.False(item.IsMap);
        Assert.Equal(0, item.AffixCount);
    }

    [Fact]
    public void Parse_Null_DoesNotThrow()
        => Assert.Equal(ItemRarity.Unknown, MapItemParser.Parse(null).Rarity);

    [Fact]
    public void Parse_CrlfText_ParsesSameAsLf()
    {
        var lf = MapItemParser.Parse(Fixture("rare-6affix-simplified.txt"));
        var crlf = MapItemParser.Parse(Fixture("rare-6affix-simplified.txt").Replace("\n", "\r\n"));

        Assert.Equal(lf.Rarity, crlf.Rarity);
        Assert.Equal(lf.AffixCount, crlf.AffixCount);
        Assert.Equal(lf.GetStatOr(MapStat.ItemQuantity, -1), crlf.GetStatOr(MapStat.ItemQuantity, -1));
    }

    // ═══ 词缀块优先于属性行 ═══════════════════════════════

    /// <summary>
    /// 词缀块与属性行同时给出同一词缀时，以<b>块</b>为准。
    /// ⚠ 这条优先级建立在「属性行是合计值」尚未证实的假设上（SPEC §7 阻塞项①）。
    /// 真机样本若证明属性行才是玩家关心的门槛口径，本用例应连同实现一起反转。
    /// </summary>
    [Fact]
    public void Stats_UsesAffixBlock_OverPropertyLine()
    {
        var item = MapItemParser.Parse(
            "稀 有 度: 稀有\n" +
            "{ +35% 物品数量 }\n" +
            "物品数量: +99%\n");

        Assert.Equal(35, item.GetStatOr(MapStat.ItemQuantity, -1));
    }

    /// <summary>块里没有的词缀，属性行负责补齐（块优先、属性行兜底）。</summary>
    [Fact]
    public void Stats_FallsBackToPropertyLine_WhenBlockMissing()
    {
        var item = MapItemParser.Parse(
            "稀 有 度: 稀有\n" +
            "{ 区域内含有额外怪物 }\n" +
            "物品数量: +28%\n");

        Assert.Equal(28, item.GetStatOr(MapStat.ItemQuantity, -1));
    }

    // ═══ 可洗练判定 ═══════════════════════════════════════

    [Fact]
    public void IsPolishable_NormalMap_True()
        => Assert.True(MapItemParser.Parse(Fixture("normal-map-quality0.txt")).IsPolishable);

    [Fact]
    public void IsPolishable_CorruptedMap_False()
        => Assert.False(MapItemParser.Parse(Fixture("rare-8affix-corrupted.txt")).IsPolishable);

    // ═══ 逐块关键词查询 ═══════════════════════════════════

    [Fact]
    public void HasAffixContaining_MatchesInsideBlock()
        => Assert.True(MapItemParser.Parse(Fixture("rare-6affix-simplified.txt"))
            .HasAffixContaining("额外怪物"));

    /// <summary>关键词跨块的「拼接」不应命中——逐块匹配的直接后果。</summary>
    [Fact]
    public void HasAffixContaining_DoesNotSpanAcrossBlocks()
    {
        var item = MapItemParser.Parse("稀 有 度: 稀有\n{ 怪物 }\n{ 数量 }\n");
        Assert.False(item.HasAffixContaining("怪物 数量"));
    }

    [Fact]
    public void HasAffixContaining_EmptyKeyword_IsFalse()
        => Assert.False(MapItemParser.Parse(Fixture("rare-6affix-simplified.txt"))
            .HasAffixContaining(string.Empty));
}
