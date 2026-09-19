using LibDat2;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 发布包里只有 exe ⇒ 运行时读 exe 旁文件的路子必然断（内置特效补丁就是这么炸的）。
/// 这里守住其余「随程序走」的资源：dat 结构定义必须能从程序集内嵌副本加载，不碰磁盘。
/// </summary>
public sealed class EmbeddedRuntimeAssetsTests
{
    [Fact]
    public void DatDefinitions_LoadFromEmbeddedResource()
    {
        DatContainer.ReloadDefinitionsFromEmbedded();

        Assert.NotNull(DatContainer.DatDefinitions);
        Assert.True(DatContainer.DatDefinitions!.ContainsKey("languages"));
    }

    [Fact]
    public void DatDefinitions_FallbackDoesNotRequireFileBesideExe()
    {
        // 无参重载过去只认 exe 旁的 DatDefinitions.json，单文件发布后必然 FileNotFound
        DatContainer.ReloadDefinitions();

        Assert.NotNull(DatContainer.DatDefinitions);
    }
}
