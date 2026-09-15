using System.IO;
using System.Text.Json;

namespace PoEToolbox.Shared;

/// <summary>
/// 已应用补丁记录：<c>&lt;Bundles2&gt;/backup/applied-patches.json</c>。
/// 补丁应用 / 还原 / 卸载时由引擎写入，界面直接读它就知道打了哪些补丁——
/// 不用为了显示状态去打开几 GB 的索引。
/// </summary>
/// <remarks>
/// 这份记录只是「账本」，不是还原依据：真正的还原仍然靠游戏索引本身与 backup/_.index.bin 基线。
/// 所以它可能和真实状态对不上（例如用启动器修复过客户端、或手动删了备份目录），
/// 「刷新补丁状态」会重新扫描索引并按真实结果校正它。
/// </remarks>
public static class FxPatchStateStore
{
    public const string FileName = "applied-patches.json";

    /// <summary>内置补丁（fx-oilmod 注册表里的）。</summary>
    public const string KindBuiltIn = "builtin";
    /// <summary>自定义补丁（用户选的 .patch.json / .zip）。</summary>
    public const string KindCustom = "custom";
    /// <summary>整包替换型补丁（包里自带 _.index.bin，不走索引级 op）。</summary>
    public const string KindRawPack = "rawpack";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public sealed record AppliedPatch(
        string Id,
        string Name,
        string Kind,
        string? Bundle,
        DateTimeOffset AppliedAt,
        /// <summary>应用时用户提供的补丁文件路径（.patch.json / .zip）；界面靠它直接还原 / 卸载第三方补丁。旧记录没有这个字段。</summary>
        string? SourceFile = null);

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<AppliedPatch> Patches { get; set; } = new();
    }

    /// <summary>记录文件路径：与基线备份放在一起，跟着游戏目录走。</summary>
    public static string FilePathOf(string gameDataPath)
        => Path.Combine(IndexBackupService.GetBackupDirectory(gameDataPath), FileName);

    /// <summary>读出已应用补丁清单；文件不存在或损坏时当作「一个都没打」，不抛异常。</summary>
    public static List<AppliedPatch> Read(string gameDataPath)
    {
        var path = FilePathOf(gameDataPath);
        if (!File.Exists(path))
            return [];
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json);
            return doc?.Patches.Where(p => !string.IsNullOrWhiteSpace(p.Id)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn($"已应用补丁记录读取失败（按「无记录」处理）：{path} — {ex.Message}");
            return [];
        }
    }

    /// <summary>记下（或刷新）一个已应用的补丁。同一个 id 重复记录只更新，不会留下重复项。</summary>
    public static void MarkApplied(string gameDataPath, AppliedPatch patch)
    {
        lock (Gate)
        {
            var patches = Read(gameDataPath);
            patches.RemoveAll(p => p.Id.Equals(patch.Id, StringComparison.OrdinalIgnoreCase));
            patches.Add(patch);
            Write(gameDataPath, patches);
        }
    }

    /// <summary>撤下一个补丁的记录（还原 / 卸载后调用）。</summary>
    public static void MarkRemoved(string gameDataPath, string patchId)
    {
        if (string.IsNullOrWhiteSpace(patchId))
            return;
        lock (Gate)
        {
            var patches = Read(gameDataPath);
            if (patches.RemoveAll(p => p.Id.Equals(patchId, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
            Write(gameDataPath, patches);
        }
    }

    /// <summary>清空记录（恢复原版索引时调用——那时候补丁全都没了）。</summary>
    public static void Clear(string gameDataPath)
    {
        lock (Gate)
        {
            var path = FilePathOf(gameDataPath);
            if (!File.Exists(path))
                return;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                FileLogger.App.Warn($"已应用补丁记录清理失败：{path} — {ex.Message}");
            }
        }
    }

    /// <summary>整体覆盖写入。给「刷新补丁状态」按扫描结果校正账本用。</summary>
    public static void SaveAll(string gameDataPath, IReadOnlyList<AppliedPatch> patches)
    {
        lock (Gate)
            Write(gameDataPath, patches.ToList());
    }

    private static void Write(string gameDataPath, List<AppliedPatch> patches)
    {
        var path = FilePathOf(gameDataPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 先写临时文件再替换：写一半被打断也不会留下损坏的账本。
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document { Patches = patches }, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 账本不是游戏数据：写失败只提示，绝不影响补丁本身的成败。
            FileLogger.App.Warn($"已应用补丁记录写入失败：{path} — {ex.Message}");
        }
    }
}
