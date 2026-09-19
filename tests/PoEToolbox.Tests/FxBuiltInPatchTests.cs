using System.IO;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 内置特效补丁曾经靠 exe 旁的 <c>Patches\</c> 副本运行，发布包只装了 exe ⇒ 启用时报
/// "Could not find file ...\oil-ground-fx-lite.patch.json"。现在补丁描述编译进程序集、运行时
/// 释放到工具箱数据目录（AppData），来源唯一，与部署形态无关。这两个用例守住这条底线。
///
/// 释放动作是真的往磁盘写，所以整类改到临时目录下跑：既不再污染用户的 AppData，
/// 也因为它读的是进程级静态路径而必须与 <see cref="ConfigServiceTests"/> 串行。
/// </summary>
[Collection(ConfigPathTestCollection.Name)]
public sealed class FxBuiltInPatchTests : IDisposable
{
    private readonly string _dir;

    public FxBuiltInPatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "poetoolbox-builtin-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ConfigService.DataDirectoryOverride = () => _dir;
    }

    public void Dispose()
    {
        ConfigService.DataDirectoryOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EveryBuiltInPatch_IsEmbeddedAndMaterializesToDisk()
    {
        Assert.NotEmpty(FxBuiltInPatches.BuiltIns);

        foreach (var def in FxBuiltInPatches.BuiltIns)
        {
            var path = FxBuiltInPatches.TryResolveBuiltInPatchPath(def.Id);
            Assert.False(string.IsNullOrWhiteSpace(path), $"内置补丁 {def.Id} 没有可用的描述文件");
            Assert.True(File.Exists(path), $"内置补丁 {def.Id} 释放失败：{path}");
            Assert.EndsWith(def.FileName, path!, StringComparison.Ordinal);

            var json = File.ReadAllText(path!);
            Assert.Contains("\"patchId\"", json);
            Assert.Contains(def.Id, json);
        }
    }

    [Fact]
    public void MaterializedPatch_IsUnderToolboxDataDirectory()
    {
        var path = FxBuiltInPatches.TryResolveBuiltInPatchPath("oil-ground-fx-lite");
        Assert.False(string.IsNullOrWhiteSpace(path));

        var full = Path.GetFullPath(path!);
        var dataDir = Path.GetFullPath(ConfigService.PatchesDirectory);
        Assert.StartsWith(dataDir, full, StringComparison.OrdinalIgnoreCase);
    }
}
