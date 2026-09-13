using System.IO;
using System.Text;
using LibBundle3;
using PoEToolbox.Shared;
using Xunit;

using Index = LibBundle3.Index;

namespace PoEToolbox.Tests;

/// <summary>
/// A patch writes into a bundle named after its <c>version</c> instead of the moment it ran, so a
/// repeated apply/revert cycle reuses one file instead of piling up bundles. And purge may only delete
/// paths the patch itself created — reverting redirects base-game files into the patch bundle, and
/// dropping those records would delete the files from the index rather than restore them.
/// </summary>
public sealed class FxPatchBundleNamingTests : IDisposable
{
    private const string SeedPath = "metadata/effects/spells/grd_zones/grd_burning01.ao";
    private const string SeedText = "{\"name\":\"loop\"}";
    private const string PatchCopy = "metadata/effects/spells/grd_zones/grd_burning01_oil.ao";
    private const string BaseTable = "data/balance/miscanimated.datc64";

    private readonly string _directory;

    public FxPatchBundleNamingTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "poetoolbox-fxname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }

    [Fact]
    public void BundlePath_ComesFromTheVersion_NotFromWhenItRan()
    {
        var patch = Patch("OilGrenade", "3");

        Assert.Equal("PATCHED/OilGrenade_v3", FxPatchEngine.BundlePathOf(patch));

        // The prefix deliberately carries no version: an older version's bundle must still be
        // recognisable as this patch's own output (that's what makes upgrading possible).
        Assert.Equal("PATCHED/OilGrenade_", FxPatchEngine.BundlePrefixOf(patch));
        Assert.StartsWith(FxPatchEngine.BundlePrefixOf(patch), FxPatchEngine.BundlePathOf(patch), StringComparison.Ordinal);
    }

    [Fact]
    public void BundlePath_SeparatesPatchesThatShareTheSameVersionNumber()
    {
        Assert.NotEqual(
            FxPatchEngine.BundlePathOf(Patch("PatchA", "1")),
            FxPatchEngine.BundlePathOf(Patch("PatchB", "1")));
    }

    [Fact]
    public void OwnedPaths_ListsOnlyFilesThePatchCreates()
    {
        var patch = Patch("TestPatch", "1");
        patch.Operations.AddRange(
        [
            new FxPatchEngine.PatchOp { Op = "addfile-derived", Src = SeedPath, Dst = PatchCopy },
            new FxPatchEngine.PatchOp { Op = "addfile-asset", Dst = "metadata/new.bin", Asset = "assets/0_new.bin" },
            // 替换型：dst 是游戏原有文件，原版字节存在 → 不能删
            new FxPatchEngine.PatchOp { Op = "addfile-asset", Dst = BaseTable, Asset = "assets/1_base.bin", OriginalAsset = "assets/1_base.orig.bin" },
            // 指针重定向与文本替换改的都是游戏原有文件 → 不能删
            new FxPatchEngine.PatchOp { Op = "patchptr-byid", Table = BaseTable, Id = "BaseOilGroundBurningEffect", OriginalPath = "o.ao", NewPath = "n.ao" },
            new FxPatchEngine.PatchOp { Op = "edittext", Path = "metadata/effects/x.ot", Old = "a", New = "b" },
        ]);

        var owned = FxPatchEngine.OwnedPathsOf(patch);

        Assert.Equal(2, owned.Count);
        Assert.Contains(PatchCopy, owned);
        Assert.Contains("metadata/new.bin", owned);
        Assert.DoesNotContain(BaseTable, owned);
        Assert.DoesNotContain("metadata/effects/x.ot", owned);
    }

    [Fact]
    public void OwnedPaths_MatchesRegardlessOfPathCasing()
    {
        var patch = Patch("TestPatch", "1");
        patch.Operations.Add(new FxPatchEngine.PatchOp
        {
            Op = "addfile-derived",
            Src = "Metadata/Effects/Spells/grd_Zones/grd_Burning01.ao",
            Dst = "Metadata/Effects/Spells/grd_Zones/grd_Burning01_oil.ao",
        });

        var owned = FxPatchEngine.OwnedPathsOf(patch);

        // 索引里的路径大小写与补丁描述未必一致，比较必须忽略大小写
        Assert.Contains("Metadata/Effects/Spells/grd_Zones/grd_Burning01_oil.ao", owned);
        Assert.Contains("metadata/effects/spells/grd_zones/grd_burning01_oil.ao", owned);
    }

    [Fact]
    public void IsPatchOwned_AcceptsItsOwnBundles_AndRejectsBaseGameFiles()
    {
        var indexPath = SeedIndex.Build(_directory, (SeedPath, Encoding.UTF8.GetBytes(SeedText)));
        var patch = Patch("TestPatch", "1");

        using (var index = new Index(indexPath, parsePaths: true))
        {
            index.PinnedWriteBundlePath = FxPatchEngine.BundlePathOf(patch);
            index.AddFile(PatchCopy, Encoding.UTF8.GetBytes("own output"));
            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);

        // 本补丁写出来的副本：可以覆盖（升版本时要靠它）
        Assert.True(FxPatchEngine.IsPatchOwned(reopened, patch, PatchCopy));
        // 游戏本体文件：不属于本补丁，绝不能覆盖
        Assert.False(FxPatchEngine.IsPatchOwned(reopened, patch, SeedPath));
        Assert.False(FxPatchEngine.IsPatchOwned(reopened, patch, "metadata/effects/spells/nope.ao"));
    }

    [Fact]
    public void IsPatchOwned_RecognisesOlderVersionsOfTheSamePatch()
    {
        var indexPath = SeedIndex.Build(_directory, (SeedPath, Encoding.UTF8.GetBytes(SeedText)));
        var v1 = Patch("TestPatch", "1");
        var v2 = Patch("TestPatch", "2");

        using (var index = new Index(indexPath, parsePaths: true))
        {
            index.PinnedWriteBundlePath = FxPatchEngine.BundlePathOf(v1);
            index.AddFile(PatchCopy, Encoding.UTF8.GetBytes("v1 output"));
            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);

        // v2 必须认得出 v1 留下的副本是自己上一版，否则升版本会被自己的旧产物挡住
        Assert.True(FxPatchEngine.IsPatchOwned(reopened, v2, PatchCopy));
        Assert.False(FxPatchEngine.IsPatchOwned(reopened, Patch("OtherPatch", "1"), PatchCopy));
    }

    private static FxPatchEngine.PatchDef Patch(string bundleName, string version)
        => new() { PatchId = "test-" + bundleName, BundleName = bundleName, Version = version };
}
