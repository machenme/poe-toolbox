using System.Text;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 悬空 PATCHED bundle 修复的引擎级集成测试：SeedIndex 造最小 Bundles2 索引 → 引擎应用词缀补丁
/// （文件重定向进 PATCHED bundle、基线自动创建）→ 删除 PATCHED 目录模拟「恢复默认游戏数据」，
/// 验证干净基线可自动修复、被污染的基线不再"假修复"（2026-09-16 词缀上色悬空 v12 bundle 的根因）。
/// </summary>
public sealed class PatchBundleRepairTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameDir;
    private readonly string _patchesDir;

    private const string TestPath = "data/statdescriptions/map_stat_descriptions.csd";
    private const string OriginalContent = "ORIGINAL-CONTENT";
    private const string ModifiedContent = "MODIFIED-CONTENT";

    public PatchBundleRepairTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-bundlerepair-" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_root, "Bundles2");
        _patchesDir = Path.Combine(_root, "patches");
        SeedIndex.Build(_gameDir, (TestPath, Encoding.UTF8.GetBytes(OriginalContent)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>应用一次词缀补丁：文件重定向进 PATCHED/affix-workbench_v1.bundle.bin，基线由引擎创建。</summary>
    private string ApplyPatch()
    {
        var jsonPath = AffixPatchBuilder.Build(
            [new AffixPatchBuilder.FileChange(TestPath, Encoding.UTF8.GetBytes(OriginalContent), Encoding.UTF8.GetBytes(ModifiedContent))],
            _patchesDir);
        RunEngine(_root, jsonPath, "apply");
        return jsonPath;
    }

    private void DeletePatchedDirectory() =>
        Directory.Delete(Path.Combine(_gameDir, "PATCHED"), recursive: true);

    private string BaselinePath => Path.Combine(_gameDir, "backup", "_.index.bin");

    private string LiveIndexPath => Path.Combine(_gameDir, "_.index.bin");

    [Fact]
    public void CleanBaseline_RestoresRedirectedFilesAndDropsDanglingBundle()
    {
        ApplyPatch();
        using (var game = GameDataAccess.OpenReadOnlyMapped(_root))
        {
            Assert.True(game.Index.TryGetFile(TestPath, out var applied));
            Assert.StartsWith("PATCHED/", applied!.BundleRecord!.Path);
        }

        DeletePatchedDirectory();

        var repaired = PatchBundleRepair.RepairIfBroken(_root, _ => { });

        Assert.True(repaired > 0);
        using (var game = GameDataAccess.OpenReadOnlyMapped(_root))
        {
            // 引用回到原版 bundle，内容回到原版；悬空的 PATCHED bundle 记录被孤儿清理移除
            Assert.Equal(OriginalContent, Encoding.UTF8.GetString(game.ReadFile(TestPath)!));
            Assert.True(game.Index.TryGetFile(TestPath, out var restored));
            Assert.False(restored!.BundleRecord!.Path.StartsWith("PATCHED/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(game.Index.Bundles.Span.ToArray(), br => br.Path.StartsWith("PATCHED/", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void PoisonedBaseline_DoesNotFakeRepair()
    {
        ApplyPatch();

        // 模拟「基线在补丁应用状态下创建」的污染：把当前（已打补丁的）索引整体覆盖到基线位置
        File.Copy(LiveIndexPath, BaselinePath, overwrite: true);
        DeletePatchedDirectory();

        var log = new List<string>();
        var repaired = PatchBundleRepair.RepairIfBroken(_root, log.Add);

        // 修复必须失败（返回 0），而不是把文件重定向回同一个丢失的 bundle 后报"已修复"
        Assert.Equal(0, repaired);
        Assert.Contains(log, l => l.Contains("污染"));
        using (var game = GameDataAccess.OpenReadOnlyMapped(_root))
        {
            Assert.True(game.Index.TryGetFile(TestPath, out var fr));
            Assert.StartsWith("PATCHED/", fr!.BundleRecord!.Path);
        }
    }

    [Fact]
    public void Begin_RefusesToBaselineAPatchedIndex()
    {
        ApplyPatch();

        // 模拟旧版本工具的残留状态：补丁已应用，但基线缺失（下轮 apply 的 Begin 会尝试补建）
        File.Delete(BaselinePath);

        using (var game = GameDataAccess.OpenReadOnlyMapped(_root))
            IndexBackupService.Begin(game);

        // 已打补丁的索引不能被固化为「原版基线」——否则之后每次恢复原版都指向丢失的补丁文件
        Assert.False(File.Exists(BaselinePath));
    }

    private static void RunEngine(string root, string jsonPath, string action)
    {
        var log = new List<string>();
        FxPatchEngine.LogSink = log.Add;
        try
        {
            var code = FxPatchEngine.Run([root, jsonPath, action], null);
            if (code != 0)
                throw new InvalidOperationException($"引擎退出码 {code}：\n" + string.Join("\n", log));
        }
        finally
        {
            FxPatchEngine.LogSink = null;
        }
    }
}
