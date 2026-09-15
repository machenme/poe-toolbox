using System.Text;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>uisettings.xml 颜色段注入/剥离：只动方案 id，游戏自带与他补丁颜色（efarm*）保留。</summary>
public sealed class UISettingsDocTests
{
    private static byte[] Doc() => DocText(
        "<?xml version=\"1.0\"?><Props id=\"PathOfExile\">",
        "\t<Font id=\"Normal\" size=\"33\" typeface=\"Fontin\"/>",
        "\t<Colour id=\"Critical\" value=\"255,0,0\"/><Colour id=\"efarmVeryLucky\" value=\"180,50,255\"/>",
        "\t<SupportsMiniMap=\"true\"/>",
        "</Props>");

    private static byte[] DocText(params string[] lines)
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
        var bytes = Doc();
        Assert.Equal(bytes, UISettingsDoc.Parse(bytes).Serialize());
    }

    [Fact]
    public void SetColors_InjectsBeforePropsClose()
    {
        var doc = UISettingsDoc.Parse(Doc());
        doc.SetColors([new AffixColorDef("VeryLucky", 180, 50, 255).ToTuple()]);

        var text = Encoding.Unicode.GetString(doc.Serialize(), 2, doc.Serialize().Length - 2);
        Assert.Contains("<Colour id=\"VeryLucky\" value=\"180,50,255\"/></Props>", text);
        Assert.Contains("<Colour id=\"efarmVeryLucky\" value=\"180,50,255\"/>", text); // 他补丁颜色保留
    }

    [Fact]
    public void SetColors_Twice_IsIdempotent()
    {
        var doc = UISettingsDoc.Parse(Doc());
        var colors = new[] { new AffixColorDef("VeryLucky", 180, 50, 255), new AffixColorDef("DangerRed", 255, 30, 30) };
        doc.SetColors(colors.Select(c => c.ToTuple()));
        var once = doc.Serialize();

        doc.SetColors(colors.Select(c => c.ToTuple()));
        Assert.Equal(once, doc.Serialize());
    }

    [Fact]
    public void SetColors_ChangedRgb_UpdatesSingleDefinition()
    {
        var doc = UISettingsDoc.Parse(Doc());
        doc.SetColors([new AffixColorDef("DangerRed", 255, 30, 30).ToTuple()]);
        doc.SetColors([new AffixColorDef("DangerRed", 0, 200, 120).ToTuple()]);

        var text = Encoding.Unicode.GetString(doc.Serialize());
        Assert.Contains("<Colour id=\"DangerRed\" value=\"0,200,120\"/>", text);
        Assert.DoesNotContain("255,30,30", text);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(text, "id=\"DangerRed\""));
    }

    [Fact]
    public void StripColors_RemovesOwned_KeepsGameColors()
    {
        var doc = UISettingsDoc.Parse(Doc());
        doc.SetColors([new AffixColorDef("LuckyPurple", 160, 0, 255).ToTuple()]);

        Assert.Equal(1, doc.StripColors(["LuckyPurple"]));

        var text = Encoding.Unicode.GetString(doc.Serialize());
        Assert.DoesNotContain("LuckyPurple", text);
        Assert.Contains("<Colour id=\"Critical\" value=\"255,0,0\"/>", text);
    }

    /// <summary>带透明度的颜色按游戏的 a,r,g,b 四段格式写入（同一色系靠 alpha 区分等级）。</summary>
    [Fact]
    public void SetColors_WithAlpha_WritesFourComponents()
    {
        var doc = UISettingsDoc.Parse(Doc());
        doc.SetColors([new AffixColorDef("LUK2", 170, 158, 130, 200).ToTuple()]);

        var raw = doc.Serialize();
        var text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        Assert.Contains("<Colour id=\"LUK2\" value=\"200,170,158,130\"/>", text);
    }

    /// <summary>alpha = 255 时保持游戏原生的 r,g,b 三段格式。</summary>
    [Fact]
    public void SetColors_Opaque_WritesThreeComponents()
    {
        var doc = UISettingsDoc.Parse(Doc());
        doc.SetColors([new AffixColorDef("Lucky", 10, 20, 30).ToTuple()]);

        var raw = doc.Serialize();
        var text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        Assert.Contains("<Colour id=\"Lucky\" value=\"10,20,30\"/>", text);
    }

    /// <summary>外部补丁写过的四段颜色，重复注入同一 id 时应被整体替换（不残留旧定义）。</summary>
    [Fact]
    public void SetColors_OverridesExternalFourComponentDefinition()
    {
        var doc = UISettingsDoc.Parse(DocText(
            "<Props id=\"PathOfExile\">",
            "\t<Colour id=\"AT1\" value=\"255,231,179,37\"/>", // 外部补丁的写法
            "</Props>"));

        doc.SetColors([new AffixColorDef("AT1", 231, 179, 37, 200).ToTuple()]);

        var raw = doc.Serialize();
        var text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        Assert.Contains("<Colour id=\"AT1\" value=\"200,231,179,37\"/>", text);
        Assert.DoesNotContain("255,231,179,37", text);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(text, "id=\"AT1\""));
    }

    [Fact]
    public void GetColors_ListsExistingDefinitions()
    {
        var doc = UISettingsDoc.Parse(Doc());
        var colors = doc.GetColors();

        Assert.Equal("255,0,0", colors["Critical"]);
        Assert.Equal("180,50,255", colors["efarmVeryLucky"]);
    }

    [Fact]
    public void SetColors_InvalidId_Throws()
    {
        var doc = UISettingsDoc.Parse(Doc());
        Assert.Throws<ArgumentException>(() => doc.SetColors([("1bad", (byte)0, (byte)0, (byte)0, (byte)255)]));
    }

    [Fact]
    public void SetColors_MissingPropsClose_Throws()
    {
        var bytes = DocText("<Props id=\"PathOfExiple\">");
        var doc = UISettingsDoc.Parse(bytes);
        Assert.Throws<InvalidOperationException>(() => doc.SetColors([("SomeColor", (byte)1, (byte)2, (byte)3, (byte)255)]));
    }

    /// <summary>外部词缀补丁重写过的 uisettings.xml 会丢 BOM（内容仍是 UTF-16LE）：
    /// 若按 UTF-8 解码会夹着 \0，连 &lt;/Props&gt; 都找不到，颜色注入必然失败。</summary>
    [Fact]
    public void Utf16NoBom_CanInjectColors_AndStaysNoBom()
    {
        var bytes = Encoding.Unicode.GetBytes(string.Join("\r\n",
            "<?xml version=\"1.0\"?>",
            "<Props id=\"PathOfExile\">",
            "\t<Colour id=\"Critical\" value=\"255,0,0\"/>",
            "</Props>") + "\r\n"); // 无 BOM

        var doc = UISettingsDoc.Parse(bytes);
        Assert.Equal(bytes, doc.Serialize());

        doc.SetColors([new AffixColorDef("VeryLucky", 180, 50, 255).ToTuple()]);
        var modified = doc.Serialize();
        Assert.False(modified.Length >= 2 && modified[0] == 0xFF && modified[1] == 0xFE);
        Assert.Contains("<Colour id=\"VeryLucky\" value=\"180,50,255\"/></Props>", Encoding.Unicode.GetString(modified));
    }
}

internal static class AffixColorDefTestExtensions
{
    public static (string Id, byte R, byte G, byte B, byte A) ToTuple(this AffixColorDef def)
        => (def.Id, def.R, def.G, def.B, def.A);
}
