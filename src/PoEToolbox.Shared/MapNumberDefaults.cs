using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibBundle3.Records;

namespace PoEToolbox.Shared;

/// <summary>「修改地图标签」写入的目标文件命名规则，供写入与恢复两侧共用。</summary>
public static class MapNumberDefaults
{
    public const string Poe1PathPrefix = "art/2ditems/maps/atlas2maps/new/mapnumbers";
    public const string Poe2PathPrefix = "art/2ditems/maps/endgamemaps/endgamemap";

    public const int Poe1MapCount = 16;
    public const int Poe2MapCount = 15;

    /// <summary>该客户端全部地图数字文件的路径（含 <c>.header</c> 低 mip 伴随文件）。</summary>
    public static IEnumerable<string> EnumeratePaths(bool isPoe2)
    {
        var (prefix, count) = isPoe2
            ? (Poe2PathPrefix, Poe2MapCount)
            : (Poe1PathPrefix, Poe1MapCount);
        for (var number = 1; number <= count; number++)
        {
            var path = prefix + number + ".dds";
            yield return path;
            yield return path + ".header";
        }
    }

    /// <summary>全部地图数字文件加低 mip 伴随文件的总数。</summary>
    public static int TargetFileCount(bool isPoe2)
        => (isPoe2 ? Poe2MapCount : Poe1MapCount) * 2;
}

public sealed record MapNumberRestoreResult(int RestoredFiles, int AlreadyOriginalFiles);

/// <summary>
/// 「修改地图标签」改动前的现场快照：把 <see cref="MapNumberDefaults.EnumeratePaths"/> 里每个文件的
/// **当时字节**原样存下来，供「恢复到上一次改动前」整体写回。
/// </summary>
/// <remarks>
/// 与基线（<c>Bundles2/backup/_.index.bin</c>）的分工：
/// <list type="bullet">
/// <item>基线记录的是**游戏原版**位置，用于「恢复游戏原版」这类全局还原；</item>
/// <item>本快照记录的是**用户按「写入地图标签」那一刻的实际内容**。用户改过之后又想撤，
/// 就该回到那时——哪怕那时客户端已经被第三方补丁改过（比如换了地图底图），
/// 恢复到原版会把第三方的东西一起抹掉，那是另一件事。</item>
/// </list>
/// 快照只在**没有快照时**建立（第一次写入前），所以连续调整字号 / 颜色十次，
/// 「恢复」始终回到第一次改动之前，而不是回到上一次微调之前。
/// 落点是游戏目录下的 <c>Bundles2/backup/map-numbers/</c>，与基线同处一处、随游戏目录走。
/// </remarks>
public static class MapNumberSnapshot
{
    private const string DirectoryName = "map-numbers";
    private const string ManifestName = "manifest.json";

    private sealed class Manifest
    {
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
        [JsonPropertyName("isPoe2")] public bool IsPoe2 { get; set; }
        [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = [];
    }

    private sealed class ManifestFile
    {
        /// <summary>快照文件名（相对快照目录），按序号命名不受游戏路径里的斜杠影响。</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>对应的游戏内路径，写回时用它定位索引记录。</summary>
        [JsonPropertyName("path")] public string Path { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>快照目录（游戏目录下，与基线同级）。</summary>
    /// <remarks>
    /// 传入的可以是「游戏数据入口目录 / <c>_.index.bin</c> / <c>Content.ggpk</c>」，先统一成具体文件路径：
    /// <see cref="IndexBackupService.GetBackupDirectory"/> 只做一次 <c>GetDirectoryName</c>，
    /// 直接喂目录会少算一层（<c>&lt;root&gt;/Bundles2</c> 会被当成 <c>&lt;root&gt;</c>）。
    /// 解析不出来时按「传进来的就是索引文件所在目录的上一级」处理，让调用方能拿到确定路径而不是异常。
    /// </remarks>
    public static string DirectoryOf(string gameDataPath)
    {
        string resolved;
        try
        {
            resolved = GameDataAccess.ResolvePath(gameDataPath);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            // 路径不是有效入口（例如直接给了 Bundles2 目录）：退回按原样解析，不把异常抛给界面。
            resolved = Path.GetFullPath(gameDataPath);
        }
        return Path.Combine(IndexBackupService.GetBackupDirectory(resolved), DirectoryName);
    }

    /// <summary>是否存在可用快照——界面靠它决定「恢复」按钮可不可点。</summary>
    public static bool Exists(string gameDataPath)
        => File.Exists(Path.Combine(DirectoryOf(gameDataPath), ManifestName));

    /// <summary>快照建立时间；没有快照时返回 null。用于按钮提示里说明"恢复到什么时候"。</summary>
    public static DateTimeOffset? CreatedAt(string gameDataPath)
    {
        var manifest = ReadManifest(gameDataPath);
        return manifest is not null
               && DateTimeOffset.TryParse(manifest.CreatedAt, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 第一次写入前建立现场快照。已存在快照时什么都不做（保持"回到最初"的语义）。
    /// </summary>
    /// <returns>本次是否真的写了快照。</returns>
    public static bool Capture(GameDataAccess gameData, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        var directory = DirectoryOf(gameData.GameDataPath);
        if (File.Exists(Path.Combine(directory, ManifestName)))
        {
            log?.Invoke("已存在改动前的快照，本次直接沿用（「恢复」始终回到第一次改动之前）。");
            return false;
        }

        var isPoe2 = gameData.IsPoe2Client;
        var paths = MapNumberDefaults.EnumeratePaths(isPoe2).ToList();
        var manifest = new Manifest
        {
            CreatedAt = DateTimeOffset.Now.ToString("o"),
            IsPoe2 = isPoe2,
        };

        // 先写内容再写 manifest：manifest 的存在即代表快照完整可用，
        // 中途失败留下的半份快照不会让「恢复」按钮亮起来。
        Directory.CreateDirectory(directory);
        var index = 0;
        foreach (var path in paths)
        {
            if (!gameData.TryGetFile(path, out var record) || record is null)
            {
                // 客户端缺这个文件（版本差异）：不记进 manifest，恢复时自然跳过。
                log?.Invoke($"[提示] 当前客户端缺少 {path}，不纳入快照。");
                continue;
            }

            var name = index.ToString("D4") + ".bin";
            File.WriteAllBytes(Path.Combine(directory, name), record.Read().ToArray());
            manifest.Files.Add(new ManifestFile { Name = name, Path = path });
            index++;
        }

        if (manifest.Files.Count == 0)
        {
            // 一个文件都没抓到：不留下空快照，否则按钮亮着却恢复不了任何东西。
            Directory.Delete(directory, recursive: true);
            throw new InvalidOperationException(
                "游戏数据里没有找到任何地图数字文件，无法保存改动前的快照。请确认游戏数据路径是否正确。");
        }

        File.WriteAllText(Path.Combine(directory, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));
        log?.Invoke($"已保存改动前的快照（{manifest.Files.Count} 个文件）。");
        return true;
    }

    /// <summary>读取快照内容：游戏路径 → 当时字节。</summary>
    public static Dictionary<string, byte[]> Read(string gameDataPath)
    {
        var directory = DirectoryOf(gameDataPath);
        var manifest = ReadManifest(gameDataPath)
            ?? throw new InvalidOperationException("没有找到改动前的快照，无法恢复。");

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Name);
            if (File.Exists(path))
                result[file.Path] = File.ReadAllBytes(path);
        }
        return result;
    }

    /// <summary>删除快照（恢复成功后调用：已经没有"上一次改动前"可回了）。</summary>
    public static void Discard(string gameDataPath)
    {
        var directory = DirectoryOf(gameDataPath);
        if (!Directory.Exists(directory))
            return;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不影响本次恢复已经完成，只影响按钮状态；记日志即可。
            FileLogger.App.Warn($"删除地图标签快照失败（不影响已完成的恢复）：{ex.Message}");
        }
    }

    private static Manifest? ReadManifest(string gameDataPath)
    {
        var path = Path.Combine(DirectoryOf(gameDataPath), ManifestName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            // 快照损坏：当作没有快照（按钮灰化），不要让它把恢复流程带进异常。
            FileLogger.App.Warn($"地图标签快照读取失败，按无快照处理：{ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// 把地图数字 DDS 与低 mip 伴随文件（<c>.header</c>）恢复到**上一次改动之前**。
/// </summary>
/// <remarks>
/// 内容来自 <see cref="MapNumberSnapshot"/>（第一次点「写入地图标签」前存下的现场），
/// 而不是游戏原版：用户改过之后想撤，就该回到他动手之前那一刻。
/// <para>
/// 写回方式与写入一致——<c>FileRecord.Write()</c>，所以不需要碰索引里其他任何记录，
/// 其他模块的修改天然不受影响。恢复成功后快照即失效（删除），按钮随之灰化；
/// 想再改就是一次全新的改动，会在那时重新存快照。
/// </para>
/// </remarks>
public static class MapNumberRestoreService
{
    /// <summary>
    /// 恢复到上一次改动前，返回实际写回的文件数。
    /// </summary>
    /// <param name="gameData">已打开为读写模式、且调用方持有其生命周期的游戏数据。</param>
    /// <param name="log">进度说明（中文、面向界面日志）。</param>
    /// <exception cref="InvalidOperationException">没有可用快照。</exception>
    public static MapNumberRestoreResult Restore(GameDataAccess gameData, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        var resolved = gameData.GameDataPath;
        if (!MapNumberSnapshot.Exists(resolved))
            throw new InvalidOperationException(
                "没有找到改动前的快照，无法恢复。需要先点「写入地图标签」完成一次改动，之后这里才能撤回到改动之前。");

        log?.Invoke("正在读取改动前的快照……");
        var snapshot = MapNumberSnapshot.Read(resolved);
        if (snapshot.Count == 0)
            throw new InvalidOperationException(
                "改动前的快照是空的（文件可能已被删除），无法恢复。");

        var missing = new List<string>();
        var alreadySame = 0;
        var changed = new List<(FileRecord Record, byte[] Content)>();

        foreach (var (path, content) in snapshot)
        {
            if (!gameData.TryGetFile(path, out var record) || record is null)
            {
                missing.Add(path);
                continue;
            }

            if (record.Read().Span.SequenceEqual(content))
            {
                alreadySame++;
                continue;
            }
            changed.Add((record, content));
        }

        if (missing.Count > 0)
            log?.Invoke($"[提示] 当前客户端缺少 {missing.Count} 个快照里的文件，已跳过：{string.Join("、", missing)}");

        if (changed.Count == 0)
        {
            // 内容本就是快照那版（例如写入后又被别的方式改回去了）：仍然算「已恢复」——
            // 快照的使命到此为止，照常失效，按钮灰化。
            MapNumberSnapshot.Discard(resolved);
            log?.Invoke("地图数字已经是改动前的内容，没有需要写回的文件。");
            return new MapNumberRestoreResult(0, alreadySame);
        }

        var backup = IndexBackupService.Begin(gameData);
        foreach (var (record, content) in changed)
            record.Write(content);
        gameData.Save();
        IndexBackupService.Complete(gameData, backup, "map-numbers-restore",
            new Dictionary<string, string> { ["files"] = changed.Count.ToString() });

        foreach (var (record, expected) in changed)
        {
            if (!record.Read().Span.SequenceEqual(expected))
                throw new InvalidDataException("恢复校验失败: " + record.Path);
        }

        MapNumberSnapshot.Discard(resolved);
        log?.Invoke($"已把 {changed.Count} 个文件（地图数字及其低 mip）恢复到上一次改动前的状态。");
        return new MapNumberRestoreResult(changed.Count, alreadySame);
    }
}
