using System.Text;
using System.Text.Json;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 词缀工作台补丁生成的引擎级集成测试：SeedIndex 造最小 Bundles2 索引，
/// 走 FxPatchEngine 的 status/apply/revert 全链路，验证产物可被引擎消费且可回滚。
/// </summary>
public sealed class AffixPatchBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameDir;
    private readonly string _patchesDir;

    private const string CsdPath = "data/statdescriptions/map_stat_descriptions.csd";
    private const string UiPath = "metadata/ui/uisettings.xml";

    public AffixPatchBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-affixpatch-" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_root, "Bundles2");
        _patchesDir = Path.Combine(_root, "patches");

        SeedIndex.Build(
            _gameDir,
            (CsdPath, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "csd-sample.csd"))),
            (UiPath, OriginalUi()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private byte[] OriginalCsd() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "csd-sample.csd"));

    private byte[] ModifiedCsd()
    {
        var doc = CsdDocument.Parse(OriginalCsd());
        Assert.True(doc.TryApplyColor("map_item_drop_rarity_+%", "VeryLucky"));
        return doc.Serialize();
    }

    private static byte[] OriginalUi()
    {
        var payload = Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?><Props id=\"PathOfExile\"></Props>");
        var result = new byte[payload.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        payload.CopyTo(result, 2);
        return result;
    }

    private byte[] ModifiedUi()
    {
        var doc = UISettingsDoc.Parse(OriginalUi());
        doc.SetColors([("VeryLucky", (byte)180, (byte)50, (byte)255, (byte)255)]);
        return doc.Serialize();
    }

    private int RunEngine(params string[] args)
    {
        var log = new List<string>();
        FxPatchEngine.LogSink = log.Add;
        try
        {
            var code = FxPatchEngine.Run(args, null);
            if (code != 0)
                throw new InvalidOperationException($"引擎退出码 {code}：\n" + string.Join("\n", log));
            return code;
        }
        finally
        {
            FxPatchEngine.LogSink = null;
        }
    }

    [Fact]
    public void Build_StatusApplyRevert_EngineFullCycle()
    {
        var jsonPath = AffixPatchBuilder.Build(
        [
            new AffixPatchBuilder.FileChange(CsdPath, OriginalCsd(), ModifiedCsd()),
            new AffixPatchBuilder.FileChange(UiPath, OriginalUi(), ModifiedUi()),
        ], _patchesDir);

        // 产物布局与实例补丁一致
        Assert.True(File.Exists(Path.Combine(_patchesDir, "affix-workbench", "assets", "0000_map_stat_descriptions.csd")));
        Assert.True(File.Exists(Path.Combine(_patchesDir, "affix-workbench", "assets", "0000_map_stat_descriptions.csd.orig")));
        Assert.True(File.Exists(Path.Combine(_patchesDir, "affix-workbench", "assets", "0001_uisettings.xml")));
        Assert.True(File.Exists(Path.Combine(_patchesDir, "affix-workbench", "assets", "0001_uisettings.xml.orig")));

        RunEngine(_root, jsonPath, "status");
        RunEngine(_root, jsonPath, "apply");

        using (var game = GameDataAccess.OpenReadOnlyMapped(_root))
        {
            Assert.Equal(ModifiedCsd(), game.ReadFile(CsdPath));
            Assert.Equal(ModifiedUi(), game.ReadFile(UiPath));
        }

        RunEngine(_root, jsonPath, "revert");
        using (var game = GameDataAccess.OpenReadOnlyMapped(_root))
        {
            Assert.Equal(OriginalCsd(), game.ReadFile(CsdPath));
        }
    }

    [Fact]
    public void Build_Twice_IncrementsVersion()
    {
        var changes = new[]
        {
            new AffixPatchBuilder.FileChange(CsdPath, OriginalCsd(), ModifiedCsd()),
        };

        AffixPatchBuilder.Build(changes, _patchesDir);
        var second = AffixPatchBuilder.Build(changes, _patchesDir);

        var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(second));
        Assert.Equal("2", json.GetProperty("Version").GetString());
        Assert.Equal("affix-workbench", json.GetProperty("BundleName").GetString());
    }
}
