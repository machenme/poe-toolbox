using System.Text;
using PoEToolbox.Plugins.AffixWorkbench;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 内置「官方原版」方案：应用 = 剥掉词缀描述文件里的<strong>全部</strong>颜色标签
/// （本工具的 + 第三方补丁的），恢复官方默认显示；uisettings 不进补丁。
/// 原版 csd 实测不含任何颜色标签，所以全剥即官方默认。
/// </summary>
public sealed class AffixOfficialRestoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _index;
    private static readonly byte[] TaggedCsd = Csd(
        "description",
        "\t1 some_stat",
        "\t1",
        "\t\t1 \"<AT1>{{提高 {0}%}} 之后再 <Tier1>{{+{0} 生命}}\"");

    public AffixOfficialRestoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-affix-restore-" + Guid.NewGuid().ToString("N"));
        _index = SeedIndex.Build(
            Path.Combine(_root, "Bundles2"),
            (AffixDataService.UiSettingsPath, Ui()),
            ("data/statdescriptions/stat_descriptions.csd", TaggedCsd));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] Ui() => Encoding.UTF8.GetBytes(
        "<Props>\r\n" +
        "  <Colour id=\"AT1\" value=\"255,231,179,37\"/>\r\n" +
        "</Props>\r\n");

    private static byte[] Csd(params string[] lines)
    {
        var text = string.Join("\r\n", lines) + "\r\n";
        return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
    }

    private AffixDataService ConnectedService()
    {
        var service = new AffixDataService();
        service.ConnectAsync(_index).GetAwaiter().GetResult();
        Assert.True(service.IsConnected);
        return service;
    }

    [Fact]
    public void ComputeChanges_StripsAllTags_AndKeepsUisettingsOut()
    {
        using var service = ConnectedService();
        var changes = service.ComputeChanges(AffixColorScheme.CreateOriginal());

        var change = Assert.Single(changes);
        Assert.Equal("data/statdescriptions/stat_descriptions.csd", change.GamePath);
        // 还原目标 = 连接时的字节（还原 = 回到上色前状态）
        Assert.True(change.Original.AsSpan().SequenceEqual(TaggedCsd));
        // 剥净后：无任何颜色标签，文本与占位符保留
        AssertDoesNotContainTag(change.Modified);
        Assert.Contains(Encoding.Unicode.GetBytes("提高 {0}%"), change.Modified);
        Assert.Contains(Encoding.Unicode.GetBytes("+{0} 生命"), change.Modified);
    }

    [Fact]
    public void ComputeChanges_OnCleanClient_Throws()
    {
        using var service = ConnectedService();
        // 先剥一遍（模拟已恢复），基线推进到干净状态后再算 = 无变更
        var first = service.ComputeChanges(AffixColorScheme.CreateOriginal());
        service.AdvanceBaseline(first);

        var ex = Assert.Throws<InvalidOperationException>(
            () => service.ComputeChanges(AffixColorScheme.CreateOriginal()));
        Assert.Contains("官方默认", ex.Message);
    }

    [Fact]
    public void ComputePreview_ShowsStrippedDocs()
    {
        using var service = ConnectedService();
        var count = service.ComputePreview(AffixColorScheme.CreateOriginal());
        Assert.Equal(1, count);
        var doc = service.TryGetPreviewDoc("data/statdescriptions/stat_descriptions.csd");
        Assert.NotNull(doc);
        AssertDoesNotContainTag(doc!.Serialize());
    }

    [Fact]
    public void OriginalScheme_PersistsThroughExportImport_AndIgnoresContent()
    {
        var path = Path.Combine(_root, "orig.json");
        AffixColorScheme.CreateOriginal().Export(path);
        var imported = AffixColorScheme.Import(path, "官方原版");

        Assert.True(imported.RestoreOriginal);
        Assert.Empty(imported.Validate());

        // 有人往里面塞了颜色/指派也不影响语义：仍然只做还原
        imported.Colors.Add(new AffixColorDef("X", 1, 2, 3));
        imported.Assignments.Add(new AffixAssignment("some_stat", "data/statdescriptions/stat_descriptions.csd", "X"));
        using var service = ConnectedService();
        var changes = service.ComputeChanges(imported);
        Assert.All(changes, c => AssertDoesNotContainTag(c.Modified));
    }

    [Fact]
    public void EnsureOriginalExists_CreatesFile_AndSurvivesReload()
    {
        var dir = Path.Combine(_root, "schemes");
        AffixColorScheme.StorageDirectoryOverride = () => dir;
        try
        {
            Assert.DoesNotContain(AffixColorScheme.OriginalName, AffixColorScheme.ListSchemeNames());
            AffixColorScheme.EnsureOriginalExists();
            AffixColorScheme.EnsureOriginalExists(); // 幂等
            Assert.Contains(AffixColorScheme.OriginalName, AffixColorScheme.ListSchemeNames());

            var loaded = AffixColorScheme.Load(AffixColorScheme.OriginalName);
            Assert.True(loaded.RestoreOriginal);
        }
        finally
        {
            AffixColorScheme.StorageDirectoryOverride = null;
        }
    }

    private static void AssertDoesNotContainTag(byte[] csdBytes)
    {
        var text = Encoding.Unicode.GetString(csdBytes);
        Assert.DoesNotContain("<AT1>{{", text);
        Assert.DoesNotContain("<Tier1>{{", text);
    }
}
