using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// Covers the two contracts the second migration step leans on:
/// <list type="bullet">
/// <item><see cref="GameDataAccess.ResolvePath"/> is the single rule for turning a user-supplied
/// path into the data file, so every caller sees the same file/directory/invalid behaviour.</item>
/// <item><see cref="GameDataLoader"/> always disposes the handle it opened — including when the
/// callback throws, which is the case a hand-written <c>using</c> block in a migrated call site
/// would silently get wrong.</item>
/// </list>
/// </summary>
/// <remarks>
/// Shares a collection with <see cref="MemoryReclaimerTests"/>: the reclaim chain is a process-wide
/// singleton, so the assertions on <see cref="GameDataAccess.HasOpenLocks"/> and on the reclaim
/// counters are only safe while these two classes run one at a time.
/// </remarks>
[Collection(GameDataTestCollection.Name)]
public sealed class GameDataLoaderTests : IDisposable
{
    private const string SeedPath = "metadata/effects/spells/grd_zones/grd_burning01.ao";
    private const string SeedText = "{\"name\":\"loop\"}";

    private readonly string _root;
    private readonly TimeSpan _firstPassDelay;
    private readonly TimeSpan _passInterval;

    public GameDataLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // Every Use schedules a reclaim; with the production timings each chain would outlive its
        // test by seconds and slow the whole suite down.
        _firstPassDelay = MemoryReclaimer.FirstPassDelay;
        _passInterval = MemoryReclaimer.PassInterval;
        MemoryReclaimer.FirstPassDelay = TimeSpan.FromMilliseconds(1);
        MemoryReclaimer.PassInterval = TimeSpan.FromMilliseconds(5);
    }

    public void Dispose()
    {
        MemoryReclaimer.FirstPassDelay = _firstPassDelay;
        MemoryReclaimer.PassInterval = _passInterval;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }

    // ── GameDataAccess.ResolvePath ─────────────────────────────

    [Fact]
    public void ResolvePath_ExplicitIndexFile_ReturnsFullyQualifiedPath()
    {
        var indexPath = BuildIndex();

        var resolved = GameDataAccess.ResolvePath(indexPath);

        Assert.Equal(Path.GetFullPath(indexPath), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void ResolvePath_ExplicitGgpkFile_ReturnsIt_RegardlessOfExtensionCase()
    {
        var ggpk = Path.Combine(_root, "Content.GGPK");
        File.WriteAllBytes(ggpk, []);

        Assert.Equal(ggpk, GameDataAccess.ResolvePath(ggpk));
    }

    [Fact]
    public void ResolvePath_DirectoryWithGgpk_ReturnsTheGgpkInside()
    {
        var dir = CreateDirectory("with-ggpk");
        var ggpk = Path.Combine(dir, "Content.ggpk");
        File.WriteAllBytes(ggpk, []);

        Assert.Equal(ggpk, GameDataAccess.ResolvePath(dir));
    }

    [Fact]
    public void ResolvePath_DirectoryWithBundles2_ReturnsTheIndexInside()
    {
        var dir = CreateDirectory("with-bundles2");
        Directory.CreateDirectory(Path.Combine(dir, "Bundles2"));
        var indexPath = SeedIndex.Build(Path.Combine(dir, "Bundles2"), (SeedPath, Encoding.UTF8.GetBytes(SeedText)));

        Assert.Equal(indexPath, GameDataAccess.ResolvePath(dir));
    }

    [Fact]
    public void ResolvePath_DirectoryWithBothFormats_PrefersTheGgpk()
    {
        var dir = CreateDirectory("with-both");
        var ggpk = Path.Combine(dir, "Content.ggpk");
        File.WriteAllBytes(ggpk, []);
        Directory.CreateDirectory(Path.Combine(dir, "Bundles2"));
        SeedIndex.Build(Path.Combine(dir, "Bundles2"), (SeedPath, Encoding.UTF8.GetBytes(SeedText)));

        Assert.Equal(ggpk, GameDataAccess.ResolvePath(dir));
    }

    [Fact]
    public void ResolvePath_MissingPath_Throws()
    {
        var missing = Path.Combine(_root, "nope", "Content.ggpk");

        Assert.Throws<FileNotFoundException>(() => GameDataAccess.ResolvePath(missing));
    }

    [Fact]
    public void ResolvePath_EmptyDirectory_Throws()
    {
        var dir = CreateDirectory("empty");

        Assert.Throws<FileNotFoundException>(() => GameDataAccess.ResolvePath(dir));
    }

    [Fact]
    public void ResolvePath_ExistingFileWithAnUnrelatedExtension_Throws()
    {
        var file = Path.Combine(_root, "notes.txt");
        File.WriteAllText(file, "not game data");

        // Guards the rule that only the two known names count: the file is there, but it is not data.
        Assert.Throws<FileNotFoundException>(() => GameDataAccess.ResolvePath(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_Blank_ThrowsArgumentException(string? path)
    {
        Assert.Throws<ArgumentException>(() => GameDataAccess.ResolvePath(path!));
    }

    [Fact]
    public void ResolvePath_MatchesWhatOpenAccepts()
    {
        // The point of ResolvePath living in GameDataAccess: it cannot drift from OpenCore.
        var indexPath = BuildIndex();

        var resolved = GameDataAccess.ResolvePath(indexPath);
        using var gd = GameDataAccess.OpenReadOnlyMapped(indexPath);

        Assert.Equal(resolved, gd.GameDataPath);
    }

    // ── GameDataLoader.ResolvePath ─────────────────────────────

    [Fact]
    public void LoaderResolvePath_ExplicitPath_DelegatesToGameDataAccess()
    {
        var dir = CreateDirectory("loader-dir");
        Directory.CreateDirectory(Path.Combine(dir, "Bundles2"));
        var indexPath = SeedIndex.Build(Path.Combine(dir, "Bundles2"), (SeedPath, Encoding.UTF8.GetBytes(SeedText)));

        Assert.Equal(indexPath, GameDataLoader.ResolvePath(dir));
        Assert.Equal(
            GameDataAccess.ResolvePath(indexPath),
            GameDataLoader.ResolvePath(indexPath));
    }

    [Fact]
    public void LoaderResolvePath_Blank_FallsBackToDetection()
    {
        // Whether a client is installed is environment-dependent; what must not happen is the blank
        // leaking down into GameDataAccess.ResolvePath and surfacing as an ArgumentException.
        var error = Record.Exception(() => GameDataLoader.ResolvePath("   "));

        if (error is not null)
            Assert.IsType<FileNotFoundException>(error);
    }

    // ── GameDataLoader.Use / UseAsync lifetime ─────────────────

    [Fact]
    public void Use_ReturnsTheCallbackResult_AndDisposesTheHandle()
    {
        var indexPath = BuildIndex();
        GameDataAccess? captured = null;

        var found = GameDataLoader.Use(indexPath, GameDataMode.Read, gd =>
        {
            captured = gd;
            Assert.True(gd.FileExists(SeedPath));
            return gd.Index.Files.Count;
        });

        Assert.Equal(1, found);
        AssertDisposed(captured);
    }

    [Fact]
    public void Use_DisposesTheHandleWhenTheCallbackThrows()
    {
        var indexPath = BuildIndex();
        GameDataAccess? captured = null;

        Assert.Throws<InvalidOperationException>(() => GameDataLoader.Use(indexPath, GameDataMode.Read, gd =>
        {
            captured = gd;
            throw new InvalidOperationException("boom");
        }));

        AssertDisposed(captured);
    }

    [Fact]
    public async Task UseAsync_DisposesTheHandleWhenTheCallbackThrows()
    {
        var indexPath = BuildIndex();
        GameDataAccess? captured = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => GameDataLoader.UseAsync(
            indexPath,
            GameDataMode.Read,
            (gd, _) =>
            {
                captured = gd;
                throw new InvalidOperationException("boom");
            }));

        AssertDisposed(captured);
    }

    [Fact]
    public async Task UseAsync_NeverRunsTheScopeOnTheCallersSynchronizationContext()
    {
        var indexPath = BuildIndex();

        // Stands in for the WPF Dispatcher: whatever a UI caller is parked on when it awaits.
        var original = SynchronizationContext.Current;
        var caller = new CountingSyncContext(original);
        SynchronizationContext? insideScope = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(caller);
            Assert.Same(caller, SynchronizationContext.Current);

            await GameDataLoader.UseAsync(indexPath, GameDataMode.Read, async (gd, _) =>
            {
                insideScope = SynchronizationContext.Current;
                Assert.True(gd.FileExists(SeedPath));
                await Task.Delay(50); // keep the caller suspended so resuming posts back
            });

            // The point of UseAsync: opening a real client takes seconds, and none of it may be
            // marshalled back onto the caller's context — that is what freezes the window.
            Assert.NotSame(caller, insideScope);
            Assert.Null(insideScope);

            // ...and the caller still resumes on its own context afterwards, which is where the
            // migrated call sites do their UI updates.
            Assert.True(caller.Posts > 0);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    [Fact]
    public void Use_LeavesNoLockBehind_WhenThePathCannotBeResolved()
    {
        Assert.Throws<FileNotFoundException>(() => GameDataLoader.Use(
            Path.Combine(_root, "missing"),
            GameDataMode.Read,
            _ => 0));

        Assert.False(GameDataAccess.HasOpenLocks);
    }

    [Fact]
    public void Use_LeavesNoLockBehind_WhenTheGgpkCannotBeParsed()
    {
        // ResolvePath is happy with the name, but the container is garbage — this is the path where
        // a half-opened handle is the risk, unlike a path that never resolved in the first place.
        var corrupt = Path.Combine(_root, "Content.ggpk");
        File.WriteAllBytes(corrupt, "this is not game data"u8.ToArray());
        var opened = false;

        Assert.False(GameDataAccess.HasOpenLocks);

        Assert.ThrowsAny<Exception>(() => GameDataLoader.Use(corrupt, GameDataMode.Read, _ =>
        {
            opened = true;
            return 0;
        }));

        Assert.False(opened);
        Assert.False(GameDataAccess.HasOpenLocks);
    }

    [Fact]
    public void Use_LeavesNoLockBehind_WhenTheBundles2IndexCannotBeMapped()
    {
        // A zero-length index (truncated or half-downloaded patch): resolution succeeds, mapping does not.
        var empty = Path.Combine(_root, "_.index.bin");
        File.WriteAllBytes(empty, []);
        var opened = false;

        Assert.False(GameDataAccess.HasOpenLocks);

        Assert.ThrowsAny<Exception>(() => GameDataLoader.Use(empty, GameDataMode.Read, _ =>
        {
            opened = true;
            return 0;
        }));

        Assert.False(opened);
        Assert.False(GameDataAccess.HasOpenLocks);
    }

    // ── automatic reclaim ──────────────────────────────────────

    [Fact]
    public async Task Use_RequestsAReclaimOnceTheDataIsClosed()
    {
        var indexPath = BuildIndex();
        await ReclaimTest.WaitForIdleAsync();
        var chains = MemoryReclaimer.ChainStarts;

        GameDataLoader.Use(indexPath, GameDataMode.Read, gd => gd.Index.Files.Count);

        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);
        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task UseAsync_RequestsAReclaimOnceTheDataIsClosed()
    {
        // The synchronous Use is the one ReplaceService will call, so both overloads have to
        // schedule a reclaim — not only the async ones.
        var indexPath = BuildIndex();
        await ReclaimTest.WaitForIdleAsync();
        var chains = MemoryReclaimer.ChainStarts;

        await GameDataLoader.UseAsync(indexPath, GameDataMode.Read, (gd, _) =>
        {
            Assert.True(gd.FileExists(SeedPath));
            return Task.CompletedTask;
        });

        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);
        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task UseAsync_Generic_RequestsAReclaimOnceTheDataIsClosed()
    {
        // Same contract as UseAsync, but through the Task<T> overload, so a later refactor that only
        // touches one of the two UseAsync overloads cannot silently drop the reclaim.
        var indexPath = BuildIndex();
        await ReclaimTest.WaitForIdleAsync();
        var chains = MemoryReclaimer.ChainStarts;

        var found = await GameDataLoader.UseAsync<int>(indexPath, GameDataMode.Read, (gd, _) =>
        {
            Assert.True(gd.FileExists(SeedPath));
            return Task.FromResult(gd.Index.Files.Count);
        });

        Assert.Equal(1, found);
        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);
        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task Use_Void_RequestsAReclaimOnceTheDataIsClosed()
    {
        // The Action<GameDataAccess> overload has its own finally/RequestReclaim path; cover it so a
        // change to that specific overload is caught.
        var indexPath = BuildIndex();
        await ReclaimTest.WaitForIdleAsync();
        var chains = MemoryReclaimer.ChainStarts;

        GameDataLoader.Use(indexPath, GameDataMode.Read, gd =>
        {
            Assert.True(gd.FileExists(SeedPath));
        });

        Assert.Equal(chains + 1, MemoryReclaimer.ChainStarts);
        await ReclaimTest.WaitForIdleAsync();
    }

    [Fact]
    public async Task Use_DoesNotRequestAReclaimWhenItNeverOpenedAnything()
    {
        await ReclaimTest.WaitForIdleAsync();
        var chains = MemoryReclaimer.ChainStarts;

        Assert.Throws<FileNotFoundException>(() => GameDataLoader.Use(
            Path.Combine(_root, "missing"),
            GameDataMode.Read,
            _ => 0));

        Assert.Equal(chains, MemoryReclaimer.ChainStarts);
    }

    // ── helpers ────────────────────────────────────────────────

    /// <summary>
    /// A disposed <see cref="GameDataAccess"/> drops its index, so anything going through it throws;
    /// and the process-wide lock count has to be back to zero.
    /// </summary>
    /// <remarks>
    /// <see cref="GameDataAccess.GameDataPath"/> deliberately keeps working after disposal — it is
    /// only a remembered string — so it is not used as the disposed signal here.
    /// </remarks>
    private static void AssertDisposed(GameDataAccess? gd)
    {
        Assert.NotNull(gd);
        Assert.Throws<ObjectDisposedException>(() => _ = gd!.Index);
        Assert.Throws<ObjectDisposedException>(() => gd!.FileExists(SeedPath));
        Assert.False(GameDataAccess.HasOpenLocks);
    }

    private string CreateDirectory(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string BuildIndex()
        => SeedIndex.Build(CreateDirectory("index"), (SeedPath, Encoding.UTF8.GetBytes(SeedText)));

    /// <summary>
    /// A <see cref="SynchronizationContext"/> standing in for the WPF Dispatcher: it counts how often
    /// work got marshalled back onto the caller, and forwards to the real one (xunit's) so the
    /// surrounding async bookkeeping keeps working.
    /// </summary>
    private sealed class CountingSyncContext : SynchronizationContext
    {
        private readonly SynchronizationContext? _inner;
        private int _posts;

        public CountingSyncContext(SynchronizationContext? inner) => _inner = inner;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            (_inner ?? new SynchronizationContext()).Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            if (_inner is null) d(state);
            else _inner.Send(d, state);
        }
    }
}
