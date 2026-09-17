using System.IO;
using LibBundle3.Records;
using PoEToolbox.Core.Assets;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public sealed record MapNumberWriteResult(
    int UpdatedFiles,
    string BaselinePath,
    string BundlePath,
    bool SnapshotCreated);

/// <summary>「写入地图标签」前的体检结果：地图文件当前内容是否还是游戏原版。</summary>
/// <param name="IsFirstWrite">本次是否是这台客户端上第一次写入（没有改动前快照）。</param>
/// <param name="ForeignFiles">当前内容与游戏原版不一致、且不是本工具产物的文件（相对路径）。
/// 第一次写入时非空 = 这些文件已被第三方补丁改过，继续写就会覆盖掉对方的内容。</param>
public sealed record MapNumberForeignContent(bool IsFirstWrite, IReadOnlyList<string> ForeignFiles);

public static class MapNumberReplacementService
{
    /// <summary>
    /// 写入前的只读体检：对比「当前内容」与「游戏原版」（取自基线索引）。
    /// </summary>
    /// <remarks>
    /// 只在第一次写入（还没有改动前快照）时才有意义——那时地图文件的内容若不是原版，
    /// 只可能来自第三方补丁或外部工具，继续写会把它们覆盖掉，界面据此给出提示。
    /// 已经有快照时跳过：当前内容本就该是本工具上一次写进去的，不算冲突。
    /// <para>
    /// 拿不到基线（缺失或来自别的客户端版本）时返回"无可比信息"（ForeignFiles 为空），
    /// 不阻断写入——体检失败不该变成拦路石。
    /// </para>
    /// </remarks>
    public static MapNumberForeignContent InspectBeforeWrite(GameDataAccess gameData, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        var resolved = gameData.GameDataPath;
        if (MapNumberSnapshot.Exists(resolved))
            return new MapNumberForeignContent(false, []);

        var isPoe2 = gameData.IsPoe2Client;
        var paths = MapNumberDefaults.EnumeratePaths(isPoe2).ToList();

        var baselinePath = IndexBackupService.GetBaselinePath(resolved);
        if (!File.Exists(baselinePath))
        {
            log?.Invoke("[提示] 没有原始索引备份，无法核对地图文件是否被其他补丁改过，直接继续。");
            return new MapNumberForeignContent(true, []);
        }

        var foreign = new List<string>();
        try
        {
            // 基线里的 bundle 路径相对 Bundles2，bundle 文件在游戏目录，必须显式指过去。
            using var baseline = GameDataAccess.OpenReadOnlyMapped(baselinePath, Path.GetDirectoryName(resolved));
            foreach (var path in paths)
            {
                if (!gameData.TryGetFile(path, out var record) || record is null)
                    continue;
                if (!baseline.Index.Files.TryGetValue(record.PathHash, out var original))
                    continue;

                // 内容比对而不是位置比对：文件可能被第三方重定向到自己的 bundle，也可能原地改写。
                if (!record.Read().Span.SequenceEqual(original.Read().Span))
                    foreign.Add(path);
            }
        }
        finally
        {
            // 基线索引常驻映射 ~100MB+，用完立刻回收（新增直接 Open* 必须补 Reclaim）。
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }

        return new MapNumberForeignContent(true, foreign);
    }

    public static MapNumberWriteResult Apply(
        GameDataAccess gameData,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        float fontSize = 28f,
        float offsetX = 0f,
        float offsetY = 0f,
        string fontFamily = "Arial",
        IReadOnlyList<DdsRgbColor>? colors = null,
        byte[]? poe2BackgroundImage = null)
    {
        var isPoe2 = gameData.IsPoe2Client;
        // 路径与数量取自 Shared 的统一定义：恢复功能（MapNumberRestoreService）必须和写入
        // 认到同一批文件，两边各写一份常量迟早会漂。
        var pathPrefix = isPoe2 ? MapNumberDefaults.Poe2PathPrefix : MapNumberDefaults.Poe1PathPrefix;
        var mapCount = isPoe2 ? MapNumberDefaults.Poe2MapCount : MapNumberDefaults.Poe1MapCount;
        if (isPoe2 && poe2BackgroundImage is null)
            throw new ArgumentException("PoE2 地图数字需要底图素材。", nameof(poe2BackgroundImage));
        if (colors is not null && colors.Count < mapCount)
            throw new ArgumentException($"必须为 1 到 {mapCount} 提供完整颜色列表。", nameof(colors));

        var files = new List<(int Number, FileRecord Record, FileRecord HeaderRecord)>();
        for (var number = 1; number <= mapCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pathPrefix + number + ".dds";
            if (!gameData.TryGetFile(path, out var record) || record is null)
                throw new FileNotFoundException("未找到目标文件: " + path);
            var headerPath = path + ".header";
            if (!gameData.TryGetFile(headerPath, out var headerRecord) || headerRecord is null)
                throw new FileNotFoundException("未找到目标文件: " + headerPath);
            files.Add((number, record, headerRecord));
        }

        var changed = new List<(FileRecord Record, byte[] Content)>();
        foreach (var (number, record, headerRecord) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = record.Read().ToArray();
            var replacement = DdsMapNumberRenderer.Render(
                original,
                number,
                fontSize,
                offsetX,
                offsetY,
                fontFamily,
                colors?[number - 1],
                isPoe2 ? poe2BackgroundImage : null);
            changed.Add((record, replacement));

            var header = headerRecord.Read().ToArray();
            var headerReplacement = DdsMapNumberRenderer.RenderSidecar(header, replacement);
            changed.Add((headerRecord, headerReplacement));
            progress?.Report($"已生成地图数字 {number} 及其低 mip");
        }

        // 改动前的现场快照：只存第一次改动前那一版（已存在就沿用），
        // 之后「恢复」才有明确的目标——回到第一次动手之前，而不是回到上一次微调之前。
        var snapshotCreated = MapNumberSnapshot.Capture(gameData,
            message => progress?.Report(message));

        var backup = IndexBackupService.Begin(gameData);
        foreach (var (record, content) in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record.Write(content);
        }

        gameData.Save();
        IndexBackupService.Complete(gameData, backup, "map-numbers", new Dictionary<string, string>
        {
            ["game"] = isPoe2 ? "poe2" : "poe1",
            ["fontFamily"] = fontFamily,
            ["fontSize"] = fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offsetX"] = offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offsetY"] = offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        foreach (var (record, expected) in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = record.Read();
            if (!actual.Span.SequenceEqual(expected))
                throw new InvalidDataException("写入校验失败: " + record.Path);
        }

        return new MapNumberWriteResult(
            files.Count,
            backup.BaselinePath,
            changed[0].Record.BundleRecord.Path,
            snapshotCreated);
    }

}
