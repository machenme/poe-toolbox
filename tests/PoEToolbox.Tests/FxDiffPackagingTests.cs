using System.IO;
using System.IO.Compression;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 补丁生成器打包约定：自定义名字为空时用默认时间戳名；压缩出的 zip 根目录只有 assets/ 与补丁描述
/// json 两项（不是多套一层目录），这样解压后能直接被补丁引擎认出来。
/// </summary>
public sealed class FxDiffPackagingTests : IDisposable
{
    private readonly string _directory;

    public FxDiffPackagingTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "poetoolbox-fxdiff-" + Guid.NewGuid().ToString("N"));
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
    public void MakePatchId_FallsBackToTimestampWhenNameIsEmpty()
    {
        Assert.StartsWith("diff-", FxDiff.MakePatchId(null));
        Assert.StartsWith("diff-", FxDiff.MakePatchId(""));
        Assert.StartsWith("diff-", FxDiff.MakePatchId("   "));
    }

    [Fact]
    public void MakePatchId_UsesCustomNameAndStripsInvalidFileNameChars()
    {
        Assert.Equal("MyPatch_v2", FxDiff.MakePatchId("  MyPatch v2 "));
        Assert.Equal("a_b_c", FxDiff.MakePatchId("a/b\\c"));
        Assert.Equal("名字Patch", FxDiff.MakePatchId("名字Patch"));
    }

    [Fact]
    public void CreateZip_PutsAssetsAndPatchJsonAtZipRoot()
    {
        var outDir = Path.Combine(_directory, "MyPatch");
        Directory.CreateDirectory(Path.Combine(outDir, "assets"));
        File.WriteAllText(Path.Combine(outDir, "MyPatch.patch.json"), "{\"patchId\":\"MyPatch\"}");
        File.WriteAllBytes(Path.Combine(outDir, "assets", "0001_grd.bin"), new byte[] { 1, 2, 3 });

        var zipPath = outDir + ".zip";
        FxDiff.CreateZip(outDir, zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "MyPatch.patch.json", "assets/0001_grd.bin" }, entries);
    }

    [Fact]
    public void CreateZip_OutputIsDirectlyLoadableByThePatchEngine()
    {
        var outDir = Path.Combine(_directory, "MyPatch2");
        Directory.CreateDirectory(Path.Combine(outDir, "assets"));
        var json = Path.Combine(outDir, "MyPatch2.patch.json");
        File.WriteAllText(json, "{\"patchId\":\"MyPatch2\"}");
        File.WriteAllBytes(Path.Combine(outDir, "assets", "0001_grd.bin"), new byte[] { 9 });

        var zipPath = outDir + ".zip";
        FxDiff.CreateZip(outDir, zipPath);

        var extracted = FxPatchEngine.ExtractZipPatch(zipPath);
        Assert.NotNull(extracted);
        Assert.True(File.Exists(extracted));
        Assert.Equal("MyPatch2.patch.json", Path.GetFileName(extracted));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(extracted)!, "assets")));
    }
}
