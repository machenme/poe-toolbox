using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>上色方案的持久化与校验。通过 StorageDirectoryOverride 隔离到临时目录。</summary>
public sealed class AffixColorSchemeTests : IDisposable
{
    private readonly string _dir;

    public AffixColorSchemeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "poetoolbox-scheme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AffixColorScheme.StorageDirectoryOverride = () => _dir;
    }

    public void Dispose()
    {
        AffixColorScheme.StorageDirectoryOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static AffixColorScheme Sample(string name) => new()
    {
        Name = name,
        Colors =
        [
            new AffixColorDef("VeryLucky", 180, 50, 255),
            new AffixColorDef("DangerRed", 255, 30, 30),
        ],
        Rules = [new AffixRuleGroup("g1", "地图危险词", "DangerRed", "地图首领", true)],
        Assignments = [new AffixAssignment("map_item_drop_rarity_+%", "data/statdescriptions/map_stat_descriptions.csd", "VeryLucky")],
    };

    [Fact]
    public void SaveLoad_RoundTrip_Equal()
    {
        var scheme = Sample("测试方案");
        scheme.Save();

        Assert.Equal(["测试方案"], AffixColorScheme.ListSchemeNames());
        var loaded = AffixColorScheme.Load("测试方案");

        Assert.Equal(scheme.Name, loaded.Name);
        Assert.Equal(scheme.Colors, loaded.Colors);
        Assert.Equal(scheme.Rules, loaded.Rules);
        Assert.Equal(scheme.Assignments, loaded.Assignments);
    }

    [Fact]
    public void ExportImport_RoundTrip_Equal()
    {
        var scheme = Sample("分享");
        var exportPath = Path.Combine(_dir, "shared.json");
        scheme.Export(exportPath);

        var imported = AffixColorScheme.Import(exportPath, "导入方案");
        Assert.Equal(scheme.Colors, imported.Colors);
        Assert.Equal("导入方案", imported.Name);
    }

    [Fact]
    public void Validate_ReportsDanglingReferences()
    {
        var scheme = Sample("bad");
        scheme.Rules.Add(new AffixRuleGroup("g2", "孤儿规则", "NoSuchColor", "词", true));

        var issues = scheme.Validate();
        Assert.Single(issues);
        Assert.Contains("NoSuchColor", issues[0]);
    }

    [Fact]
    public void PathOf_RejectsInvalidFileNameChars()
    {
        Assert.Throws<ArgumentException>(() => AffixColorScheme.PathOf("a/b"));
    }

    /// <summary>色阶由颜色命名自动推断：同前缀 + 数字成组，数字小的排前面（= 档位高、颜色亮）。</summary>
    [Fact]
    public void Ramps_AreDerivedFromColorNaming()
    {
        var scheme = new AffixColorScheme
        {
            Name = "ramps",
            Colors =
            [
                new AffixColorDef("AT1", 231, 179, 37, 255),
                new AffixColorDef("AT2", 231, 179, 37, 205),
                new AffixColorDef("AT3", 231, 179, 37, 155),
                new AffixColorDef("Lucky", 180, 50, 255), // 名字里没有数字 → 不成组
                new AffixColorDef("DF1", 0, 255, 127),    // 只有一个 → 不算色阶
            ],
        };

        var ramp = Assert.Single(scheme.Ramps());
        Assert.Equal("AT", ramp.Prefix);
        Assert.Equal(new[] { "AT1", "AT2", "AT3" }, ramp.ColorIds);
        Assert.Equal("AT（3 级）", ramp.Label);
        Assert.True(scheme.IsRampId("AT"));
        Assert.False(scheme.IsRampId("Lucky"));
        Assert.False(scheme.IsRampId("DF"));
    }

    /// <summary>带透明度的颜色完整往返（同一色系靠 alpha 区分等级深浅）。</summary>
    [Fact]
    public void SaveLoad_RoundTrip_PreservesAlpha()
    {
        var scheme = new AffixColorScheme
        {
            Name = "等级色",
            Colors =
            [
                new AffixColorDef("LUK1", 170, 158, 130, 255),
                new AffixColorDef("LUK2", 170, 158, 130, 205),
                new AffixColorDef("LUK3", 170, 158, 130, 155),
            ],
        };
        scheme.Save();

        var loaded = AffixColorScheme.Load("等级色");
        Assert.Equal(new[] { 255, 205, 155 }, loaded.Colors.Select(c => (int)c.A));
        Assert.Equal(scheme.Colors, loaded.Colors);
    }

    /// <summary>旧的方案文件没有 alpha 字段，读回来按不透明处理（向后兼容）。</summary>
    [Fact]
    public void Load_LegacyJsonWithoutAlpha_DefaultsToOpaque()
    {
        var path = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(path,
            """{"SchemaVersion":1,"Name":"legacy","Colors":[{"Id":"Lucky","R":10,"G":20,"B":30}],"Rules":[],"Assignments":[]}""");

        var loaded = AffixColorScheme.Import(path);
        var color = Assert.Single(loaded.Colors);
        Assert.Equal(255, color.A);
        Assert.False(color.HasAlpha);
    }
}
