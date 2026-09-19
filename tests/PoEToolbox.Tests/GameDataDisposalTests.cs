using System.Text;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 释放游戏数据时必须自己排一次内存回收。
/// 这条契约以前靠每个调用点手工补 <see cref="MemoryReclaimer.Reclaim"/>：漏一次就是一个常驻
/// 几百 MB 的索引，而且 <c>using</c> 块里提前 return 的路径天然会跳过块外的手工调用。
/// 现在由 <see cref="GameDataAccess.Dispose"/> 保证，新增调用点不可能再漏。
/// </summary>
[Collection(GameDataTestCollection.Name)]
public sealed class GameDataDisposalTests : IDisposable
{
    private readonly string _root;

    public GameDataDisposalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-disposal-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string BuildIndex(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "Bundles2"));
        return SeedIndex.Build(Path.Combine(dir, "Bundles2"),
            ("data/seed.txt", Encoding.UTF8.GetBytes("seed content")));
    }

    [Fact]
    public async Task Dispose_SchedulesReclaim_AndTheCountsPairUp()
    {
        await ReclaimTest.WaitForIdleAsync();

        var indexPath = BuildIndex("pairs");
        var opens = GameDataAccess.TotalOpens;
        var disposes = GameDataAccess.TotalDisposes;
        var reclaims = GameDataAccess.ReclaimsScheduled;

        using (var gd = GameDataAccess.OpenReadOnlyMapped(indexPath))
        {
            Assert.True(GameDataAccess.OpenInstanceCount >= 1);
            Assert.True(gd.FileExists("data/seed.txt"));
        }

        Assert.Equal(opens + 1, GameDataAccess.TotalOpens);
        Assert.Equal(disposes + 1, GameDataAccess.TotalDisposes);
        Assert.Equal(reclaims + 1, GameDataAccess.ReclaimsScheduled);

        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task Dispose_Twice_SchedulesOnlyOneReclaim()
    {
        await ReclaimTest.WaitForIdleAsync();

        var reclaims = GameDataAccess.ReclaimsScheduled;

        var gd = GameDataAccess.OpenReadOnlyMapped(BuildIndex("twice"));
        gd.Dispose();
        gd.Dispose();

        Assert.Equal(reclaims + 1, GameDataAccess.ReclaimsScheduled);

        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task ReclaimOnDispose_Off_LeavesTheReclaimToTheCaller()
    {
        await ReclaimTest.WaitForIdleAsync();

        var reclaims = GameDataAccess.ReclaimsScheduled;

        var gd = GameDataAccess.OpenReadOnlyMapped(BuildIndex("opt-out"));
        gd.ReclaimOnDispose = false;
        gd.Dispose();

        Assert.Equal(reclaims, GameDataAccess.ReclaimsScheduled);

        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task EarlyReturnOutOfTheUsingBlock_StillReclaims()
    {
        await ReclaimTest.WaitForIdleAsync();

        var reclaims = GameDataAccess.ReclaimsScheduled;

        // 引擎的 Pass 1 预检就是这种形状：using 块内在冲突时 return，块外那句手工回收根本走不到。
        Assert.Equal(1, OpenCheckAndReturnEarly(BuildIndex("early")));

        Assert.Equal(reclaims + 1, GameDataAccess.ReclaimsScheduled);

        await ReclaimTest.WaitForIdleAsync();
    }

    private static int OpenCheckAndReturnEarly(string indexPath)
    {
        using (var gd = GameDataAccess.OpenReadOnlyMapped(indexPath))
        {
            if (gd.FileExists("data/seed.txt"))
                return 1;
            return 0;
        }
    }
}
