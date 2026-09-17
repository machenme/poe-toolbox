using System.Text;
using PoEToolbox.Plugins.AffixWorkbench;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 「游戏里已有的颜色」这条读取链路：连接时把 <c>uisettings.xml</c> 的颜色定义，
/// 与词缀描述文件里<strong>真的写到</strong>的颜色标签求交集，只把有用的那部分喂给界面。
/// 场景来自真实第三方配色补丁（A 补丁的 AT1~AT4 / DA / DF / LUK / SP）。
/// </summary>
public sealed class AffixExternalColorTests : IDisposable
{
    private readonly string _root;
    private readonly string _index;

    public AffixExternalColorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-affix-ext-" + Guid.NewGuid().ToString("N"));
        _index = SeedIndex.Build(
            Path.Combine(_root, "Bundles2"),
            (AffixDataService.UiSettingsPath, Ui()),
            ("data/statdescriptions/stat_descriptions.csd", Csd(
                "description",
                "\t1 some_stat",
                "\t1",
                "\t\t1 \"<AT1>{{提高 {0}%}}\"")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] Ui() => Encoding.UTF8.GetBytes(
        "<Props>\r\n" +
        "  <Colour id=\"AT1\" value=\"255,231,179,37\"/>\r\n" +
        "  <Colour id=\"Unused9\" value=\"1,2,3\"/>\r\n" +
        "</Props>\r\n");

    private static byte[] Csd(params string[] lines)
    {
        var text = string.Join("\r\n", lines) + "\r\n";
        return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
    }

    [Fact]
    public async Task Connect_ReadsGameColors_ButOnlyThoseUsedByDescriptions()
    {
        using var service = new AffixDataService();
        await service.ConnectAsync(_index);

        Assert.True(service.IsConnected);
        // 全量定义照旧全部读出（第三方补丁用的四段 A,R,G,B 也要认）
        Assert.Contains(service.ExternalColors, c => c.Id == "AT1" && c.R == 231 && c.G == 179 && c.B == 37 && c.A == 255);
        Assert.Contains(service.ExternalColors, c => c.Id == "Unused9");

        // 界面上只列「被词缀真的引用到的」：游戏自带 300 多个定义全列出来没法看
        var shown = service.ReferencedExternalColors.Select(c => c.Id).ToList();
        Assert.Equal(["AT1"], shown);
        Assert.DoesNotContain("Unused9", shown);

        // 同一个文件也因此走「按文件真实分段渲染」的分支
        Assert.Contains(service.Entries, e => e.HasColorTag);
    }
}
