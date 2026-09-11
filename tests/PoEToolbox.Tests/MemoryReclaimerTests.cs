using System.Diagnostics;
using System.Threading.Tasks;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// Serialises the test classes that touch the process-wide game data state: the reclaim chain is
/// a singleton, so a chain started by one class would otherwise be observable from the other.
/// </summary>
internal static class GameDataTestCollection
{
    internal const string Name = "poetoolbox-game-data";
}

/// <summary>
/// Covers the scheduling contract <see cref="GameDataLoader"/> depends on: a reclaim is a singleton
/// chain, so asking for one after every short-lived use cannot pile up overlapping chains of forced
/// gen2 collections.
/// </summary>
[Collection(GameDataTestCollection.Name)]
public sealed class MemoryReclaimerTests : IDisposable
{
    private readonly TimeSpan _firstPassDelay;
    private readonly TimeSpan _passInterval;

    public MemoryReclaimerTests()
    {
        // Shortened per test; restored on dispose so the production timings survive the suite.
        _firstPassDelay = MemoryReclaimer.FirstPassDelay;
        _passInterval = MemoryReclaimer.PassInterval;
    }

    public void Dispose()
    {
        MemoryReclaimer.FirstPassDelay = _firstPassDelay;
        MemoryReclaimer.PassInterval = _passInterval;
    }

    [Fact]
    public async Task Reclaim_MergesBurstsOfRequests_IntoASingleChain()
    {
        await ReclaimTest.WaitForIdleAsync();

        // A long first delay keeps the chain alive while the burst arrives, which is exactly what
        // happens in production: several files released within the same few hundred milliseconds.
        MemoryReclaimer.FirstPassDelay = TimeSpan.FromMilliseconds(400);
        MemoryReclaimer.PassInterval = TimeSpan.FromMilliseconds(5);

        var chains = MemoryReclaimer.ChainStarts;

        for (var i = 0; i < 8; i++)
            MemoryReclaimer.Reclaim();

        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);
        Assert.True(MemoryReclaimer.IsScheduled);

        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task Reclaim_ConcurrentCallersFromManyThreads_StartOnlyOneChain()
    {
        await ReclaimTest.WaitForIdleAsync();
        MemoryReclaimer.FirstPassDelay = TimeSpan.FromMilliseconds(400);
        MemoryReclaimer.PassInterval = TimeSpan.FromMilliseconds(5);

        var chains = MemoryReclaimer.ChainStarts;

        Parallel.For(0, 32, _ => MemoryReclaimer.Reclaim(() => false));

        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);

        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task Reclaim_StartsAFreshChainOnceThePreviousOneHasFinished()
    {
        await ReclaimTest.WaitForIdleAsync();
        MemoryReclaimer.FirstPassDelay = TimeSpan.FromMilliseconds(1);
        MemoryReclaimer.PassInterval = TimeSpan.FromMilliseconds(5);

        var chains = MemoryReclaimer.ChainStarts;

        MemoryReclaimer.Reclaim();
        await ReclaimTest.WaitForIdleAsync();
        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);

        // The slot has to be free again — otherwise every later request would be silently swallowed
        // by a chain that is no longer running.
        MemoryReclaimer.Reclaim();
        await ReclaimTest.WaitForIdleAsync();
        Assert.Equal(chains + 2, MemoryReclaimer.ChainStarts);
    }

    [Fact]
    public async Task Reclaim_HonoursTheAbortCheck_AndRunsNoPassAtAll()
    {
        await ReclaimTest.WaitForIdleAsync();
        MemoryReclaimer.FirstPassDelay = TimeSpan.FromMilliseconds(1);
        MemoryReclaimer.PassInterval = TimeSpan.FromMilliseconds(5);

        var passes = MemoryReclaimer.Passes;

        MemoryReclaimer.Reclaim(() => true);
        await ReclaimTest.WaitForIdleAsync();

        Assert.Equal(passes, MemoryReclaimer.Passes);
    }

    [Fact]
    public async Task Reclaim_RequestDuringAChainIsServedByThatChain_NotADropOrASecondChain()
    {
        await ReclaimTest.WaitForIdleAsync();
        MemoryReclaimer.FirstPassDelay = TimeSpan.FromMilliseconds(1);
        MemoryReclaimer.PassInterval = TimeSpan.FromMilliseconds(20);

        var chains = MemoryReclaimer.ChainStarts;
        var passes = MemoryReclaimer.Passes;

        MemoryReclaimer.Reclaim();
        await ReclaimTest.WaitUntilAsync(() => MemoryReclaimer.Passes > passes);

        // More data was released while the chain was mid-flight: it must earn an extra pass inside
        // the running chain rather than starting another one or being forgotten.
        for (var i = 0; i < 5; i++)
            MemoryReclaimer.Reclaim();

        await ReclaimTest.WaitForIdleAsync();

        // 1 first pass + 2 scheduled extra passes + at least 1 merged pass.
        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);
        Assert.True(MemoryReclaimer.Passes >= passes + 4,
            $"expected at least {passes + 4} passes, saw {MemoryReclaimer.Passes}");
    }
}

/// <summary>Waits on the process-wide reclaim chain so tests do not observe each other's chains.</summary>
internal static class ReclaimTest
{
    internal static async Task WaitForIdleAsync()
    {
        var watch = Stopwatch.StartNew();
        while (MemoryReclaimer.IsScheduled && watch.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(5);

        Assert.False(MemoryReclaimer.IsScheduled, "a reclaim chain was still running");
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(2);

        Assert.True(condition(), "the expected reclaim activity never happened");
    }
}
