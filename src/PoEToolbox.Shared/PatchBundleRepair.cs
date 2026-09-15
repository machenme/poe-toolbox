using System.IO;
using LibBundle3.Records;

namespace PoEToolbox.Shared;

/// <summary>
/// 修复「索引仍引用 PATCHED/ 下的 bundle，但 bundle 物理文件已丢失」的悬空状态。
///
/// 补丁应用是把受影响文件重定向进 PATCHED bundle；bundle 文件丢失后（外部清理、客户端修复等），
/// 任何读取这些文件的操作（词缀工作台连接、特效补丁状态查询，乃至游戏本身加载画面）都会抛
/// FileNotFoundException。原始位置保留在基线索引（<c>Bundles2/backup/_.index.bin</c>）里：
/// 按 PathHash 找回原始 (bundle, offset, size) 把记录重定向回去，悬空 bundle 变空后由
/// <see cref="LibBundle3.Index.Save"/> 的孤儿清理移除。
/// </summary>
public static class PatchBundleRepair
{
    private const string PatchedPrefix = "PATCHED/";

    /// <summary>
    /// 检测并修复悬空的 PATCHED bundle 引用，返回修复的文件数。
    /// 没有悬空引用、PATCHED 目录不存在、没有基线备份或无法修复时返回 0（调用方继续抛出原异常）。
    /// </summary>
    /// <param name="gameDataPath">游戏数据路径（<c>_.index.bin</c> 或所在目录）。</param>
    /// <param name="log">进度与结果说明（中文、面向界面日志）。</param>
    public static int RepairIfBroken(string gameDataPath, Action<string>? log = null)
    {
        var resolved = GameDataAccess.ResolvePath(gameDataPath);
        var bundleDir = Path.GetDirectoryName(resolved);
        if (bundleDir is null || !File.Exists(resolved))
            return 0;

        // 快速预检：PATCHED 目录不存在就一定没有悬空引用，不必开索引。
        if (!Directory.Exists(Path.Combine(bundleDir, "PATCHED")))
            return 0;

        return GameDataLoader.Use(resolved, GameDataMode.ReadWrite, gd =>
        {
            var dangling = FindDanglingBundles(gd, bundleDir);
            if (dangling.Count == 0)
                return 0;

            var baselinePath = IndexBackupService.GetBaselinePath(resolved);
            if (!File.Exists(baselinePath))
            {
                log?.Invoke(
                    $"发现 {dangling.Count} 个补丁 bundle 文件丢失（索引仍引用它们，"
                    + string.Join("、", dangling.Select(p => p.Path)) + "），但没有原始索引备份，无法自动修复。");
                return 0;
            }

            if (PoeDetector.Default.IsPoeRunning())
                throw new InvalidOperationException(
                    "发现丢失的补丁 bundle，需要修复索引；请先退出游戏，再重新打开本页面自动修复。");

            log?.Invoke($"发现 {dangling.Count} 个补丁 bundle 文件丢失（{string.Join("、", dangling.Select(p => p.Path))}），正在从原始索引备份恢复这些文件的原始位置……");

            var baseline = GameDataAccess.OpenReadOnlyMapped(baselinePath, bundleDir);
            try
            {
                // 基线与当前索引的 bundle 序号不保证一致，按路径对应。
                var liveBundles = new Dictionary<string, BundleRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var br in gd.Index.Bundles.Span)
                    liveBundles[br.Path] = br;

                var baselineFiles = baseline.Index.Files;
                var repaired = 0;
                var unrestorable = 0;
                var restoredBundlePaths = new List<string>();
                foreach (var br in dangling)
                {
                    var bundleEmptied = true;
                    foreach (var fr in br.Files)
                    {
                        // 补丁自己新增的文件基线里没有原始位置，留给用户用「清理/还原补丁」处理。
                        if (!baselineFiles.TryGetValue(fr.PathHash, out var original)
                            || !liveBundles.TryGetValue(original.BundleRecord.Path, out var target))
                        {
                            unrestorable++;
                            bundleEmptied = false;
                            continue;
                        }
                        fr.Redirect(target, original.Offset, original.Size);
                        repaired++;
                    }
                    if (bundleEmptied)
                        restoredBundlePaths.Add(br.Path);
                }

                if (repaired == 0)
                {
                    log?.Invoke($"丢失的 bundle 里有 {unrestorable} 个补丁新增文件，备份中没有原始位置，无法自动恢复。");
                    return 0;
                }

                // 悬空 bundle 变空后由孤儿清理移除，索引与文件状态一起归位。
                gd.Save();
                PruneLedger(resolved, restoredBundlePaths);
                log?.Invoke($"已修复 {repaired} 个文件的索引引用（回到原版内容）。" +
                            (unrestorable > 0 ? $"另有 {unrestorable} 个补丁新增文件无法自动恢复。" : ""));
                return repaired;
            }
            finally
            {
                baseline.Dispose();
                // 基线索引 ~1.1GB 常驻映射，用完立刻回收（新增直接 Open* 必须补 Reclaim）。
                MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
            }
        });
    }

    /// <summary>索引里引用了、但物理文件不存在的 PATCHED bundle。</summary>
    private static List<BundleRecord> FindDanglingBundles(GameDataAccess gd, string bundleDir)
    {
        var result = new List<BundleRecord>();
        foreach (var br in gd.Index.Bundles.Span)
        {
            // BundleRecord.Path 带 .bundle.bin 后缀，相对 Bundles2 目录。
            if (!br.Path.StartsWith(PatchedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(Path.Combine(bundleDir, br.Path)))
                result.Add(br);
        }
        return result;
    }

    /// <summary>从「已应用补丁」账本里移除指向已消失 bundle 的条目，避免界面仍显示已启用。</summary>
    private static void PruneLedger(string resolved, IReadOnlyList<string> restoredBundlePaths)
    {
        try
        {
            var patches = FxPatchStateStore.Read(resolved);
            var remaining = patches
                .Where(p => p.Bundle is not { } bundle
                            || !restoredBundlePaths.Any(path => SameBundle(bundle, path)))
                .ToList();
            if (remaining.Count != patches.Count)
                FxPatchStateStore.SaveAll(resolved, remaining);
        }
        catch (Exception ex)
        {
            // 账本只供展示，清理失败不影响修复本身。
            FileLogger.App.Warn($"修复后清理补丁账本失败（不影响修复）：{ex.Message}");
        }
    }

    /// <summary>账本里的 bundle 形如 <c>PATCHED/x_v1</c>（无后缀），BundleRecord.Path 带 .bundle.bin。</summary>
    private static bool SameBundle(string a, string b)
    {
        const string suffix = ".bundle.bin";
        if (a.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            a = a[..^suffix.Length];
        if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            b = b[..^suffix.Length];
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}
