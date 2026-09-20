using System.Text;
using System.Xml.Linq;
using PoEToolbox.Plugins.Poe2Font;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>内置官方模板：内嵌资源必须加载得到，且继承链要解析成有效字号。</summary>
public sealed class Poe2FontTemplateTests
{
    [Fact]
    public void Default_LoadsEmbeddedOfficialTable()
    {
        var template = Poe2FontTemplate.Default;

        Assert.Equal(156, template.Entries.Count);
        Assert.Equal(2560, template.BaseResolution);
        Assert.Equal(33, template.ByKey["PathOfExile/Normal"].Size);
        Assert.Equal(45, template.ByKey["PathOfExile/Large"].Size);
    }

    [Fact]
    public void Default_ResolvesInheritedSizeAndTypeface()
    {
        var popup = Poe2FontTemplate.Default.ByKey["PathOfExile/ItemPopupTitle"];

        Assert.False(popup.HasDeclaredSize);
        Assert.Equal(39, popup.Size);
        Assert.Equal("Fontin Smallcaps", popup.Typeface);
    }

    [Fact]
    public void Default_KeepsSameIdPerScope()
    {
        var template = Poe2FontTemplate.Default;

        Assert.Equal(33, template.ByKey["PathOfExile/Normal"].Size);
        Assert.Equal(32, template.ByKey["PathOfExile/EShopPanel/Normal"].Size);
        Assert.Equal(35, template.ByKey["PathOfExile/ETradeMarketPanel/Normal"].Size);
        Assert.Equal(template.Entries.Count, template.ByKey.Count);
    }
}

/// <summary>写盘：字号一律以内置官方模板为基准折算成绝对值，与玩家当前文件的内容无关。</summary>
public sealed class Poe2FontServiceTests
{
    private static readonly Poe2FontOptions NoChanges = new("方正准圆", new Dictionary<string, int>());

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

    private static byte[] Client(params string[] lines) => Doc(
        ["<?xml version=\"1.0\"?>", "<Props id=\"PathOfExile\">", .. lines, "</Props>"]);

    private static byte[] Generate(byte[] source, Poe2FontOptions options)
        => Poe2FontService.GenerateBaseXml(source, options).Bytes;

    private static XDocument Parse(byte[] bytes)
        => XDocument.Parse(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));

    private static string? SizeOf(XDocument doc, string id, string scope = "PathOfExile")
        => doc.Descendants("Font")
            .Where(font => (string?)font.Attribute("id") == id
                && Poe2FontService.ScopeOf(font) == scope)
            .Select(font => (string?)font.Attribute("size"))
            .SingleOrDefault();

    [Fact]
    public void Generate_SetsTypefaceOnEveryFontNodeThatDeclaresOne()
    {
        var doc = Parse(Generate(Client(
            "<Font id=\"Normal\" typeface=\"Fontin\" size=\"33\"/>",
            "<Font id=\"ItemPopupTitle\" inherits=\"LargeNormalSC\"/>"), NoChanges));

        // 纯继承条目原本就没有 typeface，不替它凭空加一个：它的字形由父条目决定。
        Assert.Equal(2, doc.Descendants("Font").Count());
        Assert.All(doc.Descendants("Font").Where(font => font.Attribute("typeface") is not null), font =>
            Assert.Equal("方正准圆", (string?)font.Attribute("typeface")));
        Assert.Null(doc.Descendants("Font").Single(f => (string?)f.Attribute("id") == "ItemPopupTitle").Attribute("typeface"));
    }

    [Fact]
    public void Generate_WritesRequestedSizeAndRecordsChangeAgainstOfficial()
    {
        var source = Client("<Font id=\"Normal\" typeface=\"Fontin\" size=\"33\"/>");
        var result = Poe2FontService.GenerateBaseXml(source,
            new Poe2FontOptions("方正准圆", new Dictionary<string, int> { ["PathOfExile/Normal"] = 41 }));

        Assert.Equal("41", SizeOf(Parse(result.Bytes), "Normal"));
        var change = Assert.Single(result.Changes);
        Assert.Equal("PathOfExile/Normal", change.Key);
        Assert.Equal(33, change.OfficialSize);
        Assert.Equal(41, change.TargetSize);
    }

    [Fact]
    public void Generate_PullsModdedClientBackToOfficial()
    {
        // 基线备份可能是在第三方改版上首次应用时存的：已知条目必须被拉回官方值。
        var source = Client("<Font id=\"Normal\" typeface=\"Fontin\" size=\"40\"/>");

        Assert.Equal("33", SizeOf(Parse(Generate(source, NoChanges)), "Normal"));
        Assert.Empty(Poe2FontService.GenerateBaseXml(source, NoChanges).Changes);
    }

    [Fact]
    public void Generate_LeavesInheritOnlyEntryAloneUntilOverridden()
    {
        var source = Client(
            "<Font id=\"LargeNormalSC\" typeface=\"Fontin Smallcaps\" size=\"39\"/>",
            "<Font id=\"ItemPopupTitle\" inherits=\"LargeNormalSC\"/>");

        var untouched = Parse(Generate(source, NoChanges));
        Assert.Null(untouched.Descendants("Font").Single(f => (string?)f.Attribute("id") == "ItemPopupTitle").Attribute("size"));

        var pinned = Parse(Generate(source, new Poe2FontOptions("方正准圆",
            new Dictionary<string, int> { ["PathOfExile/ItemPopupTitle"] = 52 })));
        Assert.Equal("52", (string?)pinned.Descendants("Font")
            .Single(f => (string?)f.Attribute("id") == "ItemPopupTitle").Attribute("size"));
    }

    [Fact]
    public void Generate_KeepsEntriesMissingFromTemplate()
    {
        var source = Client("<Font id=\"BrandNewFontFromNextLeague\" typeface=\"Fontin\" size=\"27\"/>");
        var result = Poe2FontService.GenerateBaseXml(source,
            new Poe2FontOptions("方正准圆", new Dictionary<string, int> { ["PathOfExile/BrandNewFontFromNextLeague"] = 40 }));

        Assert.Equal(1, result.UnknownEntryCount);
        Assert.Equal("27", SizeOf(Parse(result.Bytes), "BrandNewFontFromNextLeague"));
    }

    [Fact]
    public void Generate_DistinguishesNestedScope()
    {
        var source = Client(
            "<Font id=\"Normal\" typeface=\"Fontin\" size=\"33\"/>",
            "<Props id=\"EShopPanel\">",
            "\t<Font id=\"Normal\" typeface=\"Fontin\" size=\"32\"/>",
            "</Props>");
        var result = Poe2FontService.GenerateBaseXml(source,
            new Poe2FontOptions("方正准圆", new Dictionary<string, int> { ["PathOfExile/EShopPanel/Normal"] = 45 }));

        var doc = Parse(result.Bytes);
        Assert.Equal("33", SizeOf(doc, "Normal"));
        Assert.Equal("45", SizeOf(doc, "Normal", "PathOfExile/EShopPanel"));
    }

    [Fact]
    public void Generate_IsIdempotent()
    {
        var source = Client(
            "<Font id=\"Normal\" typeface=\"Fontin\" size=\"33\"/>",
            "<FallbackFont id=\"CJK\" ranges=\"CJK\" fonts=\"Noto Sans CJK TC\"/>",
            "<FallbackFont id=\"Any\" ranges=\"Any\" fonts=\"Arial\"/>");
        var options = new Poe2FontOptions("方正准圆", new Dictionary<string, int> { ["PathOfExile/Normal"] = 41 });

        var first = Generate(source, options);
        var second = Generate(Generate(first, options), options);

        Assert.Equal(first, second);
        Assert.Equal(0xFF, first[0]);
        Assert.Equal(0xFE, first[1]);
    }

    [Fact]
    public void Generate_PromotesTypefaceIntoCjkFallback()
    {
        var source = Client("<FallbackFont id=\"CJK\" ranges=\"CJK\" fonts=\"Noto Sans CJK TC,Simsun\"/>");

        var doc = Parse(Generate(source, NoChanges));

        Assert.Equal("方正准圆,Noto Sans CJK TC,Simsun",
            (string?)doc.Descendants("FallbackFont").Single().Attribute("fonts"));
    }

    [Fact]
    public void Generate_RejectsOutOfRangeSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Poe2FontService.GenerateBaseXml(
            Client("<Font id=\"Normal\" typeface=\"Fontin\" size=\"33\"/>"),
            new Poe2FontOptions("方正准圆", new Dictionary<string, int> { ["PathOfExile/Normal"] = 0 })));
    }
}

/// <summary>批量调整：状态是每行的绝对字号，倍率与偏移都只是改写它的入口。</summary>
public sealed class Poe2FontBatchTests
{
    private static Poe2FontPreviewRow Row(string key)
        => new(Poe2FontTemplate.Default.ByKey[key], null);

    [Fact]
    public void Shift_CumulatesAgainstOfficial()
    {
        var normal = Row("PathOfExile/Normal");

        Poe2FontBatch.Shift([normal], 2);
        Poe2FontBatch.Shift([normal], 2);
        Poe2FontBatch.Shift([normal], 2);

        Assert.Equal(39, normal.TargetSize);
        Assert.Equal(33, normal.OfficialSize);
        Assert.Equal(39, Poe2FontBatch.ToSizeMap([normal])["PathOfExile/Normal"]);
        Assert.Empty(Poe2FontBatch.ToSizeMap([Row("PathOfExile/Normal")]));
    }

    [Fact]
    public void Shift_SkipsInheritOnlyRowsSoTheyFollowTheirParent()
    {
        var rows = new[] { Row("PathOfExile/LargeNormalSC"), Row("PathOfExile/ItemPopupTitle") };

        Poe2FontBatch.Shift(rows, 4);

        Assert.Equal(43, rows[0].TargetSize);
        Assert.Equal(39, rows[1].TargetSize);
        Assert.True(rows[1].FollowsInheritance);
    }

    [Fact]
    public void ScalePercent_IsAbsoluteRatherThanCompounding()
    {
        var rows = new[] { Row("PathOfExile/Normal"), Row("PathOfExile/Large") };

        Poe2FontBatch.ScalePercent(rows, 150);
        var afterOnce = rows.Select(row => row.TargetSize).ToArray();
        Poe2FontBatch.ScalePercent(rows, 150);

        Assert.Equal(new[] { 50, 68 }, afterOnce);
        Assert.Equal(afterOnce, rows.Select(row => row.TargetSize).ToArray());
    }

    [Fact]
    public void ToSizeMap_HoldsOnlyDeviationsAndResetClearsIt()
    {
        var rows = new[] { Row("PathOfExile/Normal"), Row("PathOfExile/Small") };

        Assert.Empty(Poe2FontBatch.ToSizeMap(rows));
        rows[0].Edit = "30";
        Assert.Equal(30, Poe2FontBatch.ToSizeMap(rows)["PathOfExile/Normal"]);

        Poe2FontBatch.ResetToOfficial(rows);
        Assert.Empty(Poe2FontBatch.ToSizeMap(rows));
    }

    [Fact]
    public void Edit_ClampsToWritableRange()
    {
        var normal = Row("PathOfExile/Normal");

        normal.Edit = "9999";

        Assert.Equal(Poe2FontService.MaxFontSize, normal.TargetSize);
    }
}
