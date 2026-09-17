using System.Text;
using PoEToolbox.Plugins.DataBrowser.Services;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 「恢复到上一次改动前」的集成测试。
/// <para>
/// 语义：第一次点「写入地图标签」时把当时的文件内容存成快照；「恢复」把快照写回并让快照失效。
/// 所以这里验证三件事——写入真的会存快照、恢复真的回到那份快照、恢复之后按钮该灰（快照没了）。
/// </para>
/// 种子索引用 <see cref="SeedIndex.Build"/> 造，写入走生产路径 <c>FileRecord.Write</c>，
/// 快照与恢复走 <see cref="MapNumberSnapshot"/> / <see cref="MapNumberRestoreService"/> 的真实实现。
/// </summary>
public sealed class MapNumberRestoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameDir;

    public MapNumberRestoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-maprestore-" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_root, "Bundles2");
        var seeds = new List<(string Path, byte[] Content)>
        {
            // 一个与地图标签无关的文件：确认整个流程不会顺手改别的模块的东西。
            ("data/balance/baseitemtypes.datc64", Encoding.UTF8.GetBytes("UNRELATED-TABLE")),
        };
        foreach (var path in MapNumberDefaults.EnumeratePaths(isPoe2: true))
        {
            seeds.Add((path, Encoding.UTF8.GetBytes(OriginalContentOf(path))));
        }
        SeedIndex.Build(_gameDir, seeds.ToArray());

        using var game = GameDataAccess.Open(_root);
        IndexBackupService.Begin(game);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string OriginalContentOf(string path) => "ORIGINAL:" + path;

    private const string WrittenPrefix = "WRITTEN:";

    private static string WrittenContentOf(string path) => WrittenPrefix + path;

    /// <summary>记录文件当前的落点（bundle + 偏移 + 长度），用来确认恢复走的是内容写回而不是原地不动。</summary>
    private Dictionary<string, (string Bundle, int Offset, int Size)> LocationsOf()
    {
        var result = new Dictionary<string, (string, int, int)>(StringComparer.OrdinalIgnoreCase);
        using var game = GameDataAccess.OpenReadOnlyMapped(_root);
        foreach (var path in MapNumberDefaults.EnumeratePaths(isPoe2: true))
        {
            Assert.True(game.Index.TryGetFile(path, out var record));
            result[path] = (record!.BundleRecord.Path, record.Offset, record.Size);
        }
        return result;
    }

    /// <summary>
    /// 模拟一次「写入地图标签」：存快照（与生产一致，第一次才算）→ 用 FileRecord.Write 改写内容。
    /// </summary>
    private void SimulateWrite(string tag)
    {
        using var game = GameDataAccess.Open(_root);
        MapNumberSnapshot.Capture(game);
        foreach (var path in MapNumberDefaults.EnumeratePaths(isPoe2: true))
        {
            Assert.True(game.Index.TryGetFile(path, out var record));
            record!.Write(Encoding.UTF8.GetBytes(tag + path));
        }
        game.Save();
    }

    private string ReadContent(string path)
    {
        using var game = GameDataAccess.OpenReadOnlyMapped(_root);
        return Encoding.UTF8.GetString(game.ReadFile(path)!);
    }

    [Fact]
    public void Capture_TakesSnapshotAndIsIgnoredOnLaterWrites()
    {
        Assert.False(MapNumberSnapshot.Exists(_root));

        SimulateWrite("FIRST:");
        Assert.True(MapNumberSnapshot.Exists(_root));

        // 连续调整不应覆盖存档：「恢复」要回到第一次改动之前，而不是上一次微调之前。
        SimulateWrite("SECOND:");
        using var game = GameDataAccess.OpenReadOnlyMapped(_root);
        var snapshot = MapNumberSnapshot.Read(_root);
        Assert.Equal(MapNumberDefaults.TargetFileCount(isPoe2: true), snapshot.Count);
        foreach (var path in MapNumberDefaults.EnumeratePaths(isPoe2: true))
            Assert.Equal(OriginalContentOf(path), Encoding.UTF8.GetString(snapshot[path]));
    }

    [Fact]
    public void Restore_GoesBackToTheContentSavedBeforeFirstWrite()
    {
        SimulateWrite("FIRST:");
        Assert.Equal("FIRST:" + MapNumberDefaults.Poe2PathPrefix + "1.dds",
            ReadContent(MapNumberDefaults.Poe2PathPrefix + "1.dds"));

        var log = new List<string>();
        MapNumberRestoreResult result;
        using (var game = GameDataAccess.Open(_root))
            result = MapNumberRestoreService.Restore(game, log.Add);

        Assert.Equal(MapNumberDefaults.TargetFileCount(isPoe2: true), result.RestoredFiles);
        Assert.Equal(0, result.AlreadyOriginalFiles);

        foreach (var path in MapNumberDefaults.EnumeratePaths(isPoe2: true))
            Assert.Equal(OriginalContentOf(path), ReadContent(path));

        // 与地图标签无关的文件不受影响
        Assert.Equal("UNRELATED-TABLE", ReadContent("data/balance/baseitemtypes.datc64"));
    }

    [Fact]
    public void Restore_ReturnsMultiRoundEditsToTheFirstWriteBaseline()
    {
        SimulateWrite("ROUND1:");
        SimulateWrite("ROUND2:");
        Assert.Equal("ROUND2:" + MapNumberDefaults.Poe2PathPrefix + "3.dds",
            ReadContent(MapNumberDefaults.Poe2PathPrefix + "3.dds"));

        using (var game = GameDataAccess.Open(_root))
            MapNumberRestoreService.Restore(game);

        // 回到 ROUND1 之前，也就是游戏原本的内容——不是回到 ROUND1。
        foreach (var path in MapNumberDefaults.EnumeratePaths(isPoe2: true))
            Assert.Equal(OriginalContentOf(path), ReadContent(path));
    }

    [Fact]
    public void Restore_DiscardsSnapshotSoItCannotBeRepeated()
    {
        SimulateWrite("FIRST:");

        using (var game = GameDataAccess.Open(_root))
            MapNumberRestoreService.Restore(game);

        // 快照用掉即失效：按钮该灰，界面据此判断。
        Assert.False(MapNumberSnapshot.Exists(_root));
        Assert.Null(MapNumberSnapshot.CreatedAt(_root));

        using (var game = GameDataAccess.Open(_root))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => MapNumberRestoreService.Restore(game));
            Assert.Contains("没有找到改动前的快照", ex.Message);
        }
    }

    [Fact]
    public void Restore_KeepsSnapshotWhenContentAlreadyMatches()
    {
        // 快照存在，但内容恰好就是快照那版（例如写入后又被别的方式改了回来）：
        // 仍然算恢复完成，快照照常失效，不该卡在「永远可点但点了没事发生」。
        using (var game = GameDataAccess.Open(_root))
            MapNumberSnapshot.Capture(game);

        var log = new List<string>();
        MapNumberRestoreResult result;
        using (var game = GameDataAccess.Open(_root))
            result = MapNumberRestoreService.Restore(game, log.Add);

        Assert.Equal(0, result.RestoredFiles);
        Assert.Equal(MapNumberDefaults.TargetFileCount(isPoe2: true), result.AlreadyOriginalFiles);
        Assert.False(MapNumberSnapshot.Exists(_root));
        Assert.Contains(log, l => l.Contains("已经是改动前的内容"));
    }

    [Fact]
    public void Restore_ThrowsWithActionableMessageWhenNoSnapshot()
    {
        using var game = GameDataAccess.Open(_root);
        var ex = Assert.Throws<InvalidOperationException>(() => MapNumberRestoreService.Restore(game));
        Assert.Contains("没有找到改动前的快照", ex.Message);
        Assert.Contains("写入地图标签", ex.Message);
    }

    [Fact]
    public void InspectBeforeWrite_IsSilentWhileClientIsUntouched()
    {
        using var game = GameDataAccess.Open(_root);
        var inspection = MapNumberReplacementService.InspectBeforeWrite(game);

        Assert.True(inspection.IsFirstWrite);
        Assert.Empty(inspection.ForeignFiles);
    }

    [Fact]
    public void InspectBeforeWrite_ReportsFilesChangedBySomethingElse()
    {
        // 直接改内容但**不存快照** = 模拟第三方补丁改了地图文件。
        using (var game = GameDataAccess.Open(_root))
        {
            Assert.True(game.Index.TryGetFile(MapNumberDefaults.Poe2PathPrefix + "7.dds", out var record));
            record!.Write(Encoding.UTF8.GetBytes("FOREIGN"));
            game.Save();
        }

        Assert.False(MapNumberSnapshot.Exists(_root));
        using (var game = GameDataAccess.Open(_root))
        {
            var inspection = MapNumberReplacementService.InspectBeforeWrite(game);
            Assert.True(inspection.IsFirstWrite);
            Assert.Equal([MapNumberDefaults.Poe2PathPrefix + "7.dds"], inspection.ForeignFiles);
        }
    }

    [Fact]
    public void InspectBeforeWrite_SkipsTheCheckOnceASnapshotExists()
    {
        // 本工具改过（快照在）时，当前内容本就该是上次写进去的，不该被当成"别人的改动"。
        SimulateWrite("FIRST:");

        using var game = GameDataAccess.Open(_root);
        var inspection = MapNumberReplacementService.InspectBeforeWrite(game);
        Assert.False(inspection.IsFirstWrite);
        Assert.Empty(inspection.ForeignFiles);
    }

    [Fact]
    public void Restore_ReportsFilesMissingFromTheClientInsteadOfFailing()
    {
        // 客户端只有 15 个里的前 5 个（不同版本 / 文件被删）：其余跳过，能恢复的照常恢复。
        var wanted = Enumerable.Range(1, 5)
            .SelectMany(n => new[]
            {
                MapNumberDefaults.Poe2PathPrefix + n + ".dds",
                MapNumberDefaults.Poe2PathPrefix + n + ".dds.header",
            })
            .ToList();
        var seeds = wanted
            .Select(p => (p, Encoding.UTF8.GetBytes(OriginalContentOf(p))))
            // 客户端识别靠这张表（GameDataAccess.IsPoe2Client），没有它会被当成 PoE1。
            .Append(("data/balance/baseitemtypes.datc64", Encoding.UTF8.GetBytes("TABLE")))
            .ToArray();
        var absent = MapNumberDefaults.EnumeratePaths(isPoe2: true).Except(wanted).ToList();
        Assert.NotEmpty(absent);

        var root = Path.Combine(Path.GetTempPath(), "poetoolbox-maprestore-partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gameDir = Path.Combine(root, "Bundles2");
            SeedIndex.Build(gameDir, seeds);
            using (var g = GameDataAccess.Open(root))
            {
                // 快照里只有客户端实际存在的那些文件（Capture 会跳过缺失的并记日志）。
                var log = new List<string>();
                MapNumberSnapshot.Capture(g, log.Add);
                Assert.Contains(log, l => l.Contains("缺少"));
                foreach (var path in wanted)
                {
                    Assert.True(g.Index.TryGetFile(path, out var record));
                    record!.Write(Encoding.UTF8.GetBytes(WrittenContentOf(path)));
                }
                g.Save();
            }

            var restoreLog = new List<string>();
            MapNumberRestoreResult result;
            using (var game = GameDataAccess.Open(root))
                result = MapNumberRestoreService.Restore(game, restoreLog.Add);

            Assert.Equal(wanted.Count, result.RestoredFiles);
            using (var game = GameDataAccess.OpenReadOnlyMapped(root))
            {
                foreach (var path in wanted)
                    Assert.Equal(OriginalContentOf(path), Encoding.UTF8.GetString(game.ReadFile(path)!));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
