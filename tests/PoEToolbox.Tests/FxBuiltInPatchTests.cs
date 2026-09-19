using System.IO;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 内置特效补丁曾经靠 exe 旁的 <c>Patches\</c> 副本运行，发布包只装了 exe ⇒ 启用时报
/// "Could not find file ...\oil-ground-fx-lite.patch.json"。现在补丁描述编译进程序集、运行时
/// 释放到工具箱数据目录（AppData），来源唯一，与部署形态无关。这两个用例守住这条底线。
/// </summary>
public sealed class FxBuiltInPatchTests
{
    [Fact]
    public void EveryBuiltInPatch_IsEmbeddedAndMaterializesToDisk()
    {
        Assert.NotEmpty(FxPatchEngine.BuiltIns);

        foreach (var def in FxPatchEngine.BuiltIns)
        {
            var path = FxPatchEngine.TryResolveBuiltInPatchPath(def.Id);
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
        var path = FxPatchEngine.TryResolveBuiltInPatchPath("oil-ground-fx-lite");
        Assert.False(string.IsNullOrWhiteSpace(path));

        var full = Path.GetFullPath(path!);
        var dataDir = Path.GetFullPath(ConfigService.PatchesDirectory);
        Assert.StartsWith(dataDir, full, StringComparison.OrdinalIgnoreCase);
    }
}
