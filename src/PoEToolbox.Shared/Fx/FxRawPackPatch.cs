using System.IO;
using System.Security.Cryptography;

namespace PoEToolbox.Shared;

/// <summary>
/// 整包替换型补丁：作者直接打包 <c>_.index.bin</c> + bundle 文件，没有 <c>.patch.json</c>。
/// 引擎不解析语义，只做「备份原版 → 覆盖 → 按清单还原」，因此必须能脱离补丁包还原（靠账本 + manifest）。
/// </summary>
/// <remarks>
/// S6 从 <see cref="FxPatchEngine"/> 原样搬出，方法体未改。这是引擎里最大的一块独立逻辑，
/// 与普通补丁只共用日志出口（<c>FxPatchEngine.Log</c> / <c>LogErr</c>）和状态存储。
/// </remarks>
internal static class FxRawPackPatch
{
    // ═══ 整包替换型补丁（包内自带 _.index.bin + bundle 文件）══════════
    /// <summary>补丁包里的一个待覆盖文件：相对路径以 <c>_.index.bin</c> 所在目录为根。</summary>
    internal sealed record RawPackFile(string RelativePath, string SourcePath);

    internal sealed record RawPack(string PatchId, IReadOnlyList<RawPackFile> Files);

    private const string RawManifestFileName = "manifest.txt";

    /// <summary>
    /// 识别"整包替换型"补丁：目录里有 <c>_.index.bin</c> 且至少还有一个其他 <c>.bin</c>。
    /// 以 <c>_.index.bin</c> 所在的那层为根（作者打包时常带一层 <c>bundles2/</c>），
    /// 其余文件按该根的相对路径原样落进游戏索引目录。
    /// </summary>
    internal static RawPack? TryDetectRawPack(string patchDir, string? patchIdHint)
    {
        var indexFile = Directory.EnumerateFiles(patchDir, "_.index.bin", SearchOption.AllDirectories)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "_.index.bin", StringComparison.OrdinalIgnoreCase));
        if (indexFile is null)
            return null;

        var root = Path.GetDirectoryName(indexFile)!;
        var bundles = Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories)
            .Where(p => !string.Equals(Path.GetFileName(p), "_.index.bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (bundles.Count == 0)
            return null;

        var files = new List<RawPackFile> { new(Path.GetFileName(indexFile), indexFile) };
        foreach (var bundle in bundles)
            files.Add(new RawPackFile(Path.GetRelativePath(root, bundle), bundle));

        return new RawPack(FxDiff.MakePatchId(patchIdHint), files);
    }

    internal static int RunRawPack(string resolved, RawPack pack, string action, string? sourceFile = null)
    {
        if (!resolved.EndsWith(".index.bin", StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
        {
            FxPatchEngine.LogErr("[中止] 整包替换补丁只支持把索引放在独立文件里的客户端（Bundles2/_.index.bin）。");
            return 1;
        }

        FxPatchEngine.Log($"[整包替换] {pack.PatchId}：{pack.Files.Count} 个文件（含索引本体）");
        return action switch
        {
            "status" => RawStatus(resolved, pack),
            "apply" => RawApply(resolved, pack, sourceFile),
            "revert" => RawRevert(resolved, pack),
            _ => RawNoBundleOperation(action),
        };
    }

    private static int RawNoBundleOperation(string action)
    {
        FxPatchEngine.Log($"[提示] 整包替换补丁不写 PATCHED bundle，{action} 对它不适用；要撤销请点「还原补丁」。");
        return 0;
    }

    /// <summary>应用：先把游戏目录里的同名文件备份到 <c>backup/&lt;补丁名&gt;/</c>，再逐个覆盖。</summary>
    private static int RawApply(string resolved, RawPack pack, string? sourceFile = null)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            FxPatchEngine.LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再应用补丁。");
            return 1;
        }

        FxPatchEngine.Log("[提示] 整包替换会整体替换游戏索引，可能覆盖已应用的其他补丁（词缀上色、技能特效等）写入的内容；受影响的补丁之后需要重新应用。");

        var indexDir = Path.GetDirectoryName(resolved)!;
        var backupDir = Path.Combine(IndexBackupService.GetBackupDirectory(resolved), pack.PatchId);
        var baselinePath = IndexBackupService.GetBaselinePath(resolved);
        Directory.CreateDirectory(backupDir);

        var manifest = new List<string> { "# 整包替换还原清单：kind\t相对路径\t备份来源" };
        foreach (var file in pack.Files)
        {
            var target = Path.Combine(indexDir, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            string kind, backupSource = "";
            if (File.Exists(target))
            {
                var backupFile = Path.Combine(backupDir, file.RelativePath);
                // 索引本体：原版基线 backup/_.index.bin 是特殊用途的那份，内容一致时直接复用，不重复占磁盘
                if (string.Equals(file.RelativePath, Path.GetFileName(resolved), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(baselinePath) && SameContent(target, baselinePath))
                {
                    backupSource = "baseline";
                    FxPatchEngine.Log($"[备份] {file.RelativePath} 与原版基线一致，复用 {Path.GetFileName(baselinePath)}");
                }
                else if (File.Exists(backupFile))
                {
                    backupSource = file.RelativePath;
                    FxPatchEngine.Log($"[备份] {file.RelativePath} 之前已备份，保留最早的原始版本");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(target, backupFile);
                    backupSource = file.RelativePath;
                    FxPatchEngine.Log($"[备份] {file.RelativePath} → backup/{pack.PatchId}/{file.RelativePath}");
                }
                kind = "replaced";
            }
            else
            {
                kind = "added";
                FxPatchEngine.Log($"[备份] 游戏目录原本没有 {file.RelativePath}（补丁新增），无需备份");
            }

            File.Copy(file.SourcePath, target, overwrite: true);
            FxPatchEngine.Log($"[覆盖] {file.RelativePath}（{new FileInfo(file.SourcePath).Length:N0} B）");
            manifest.Add($"{kind}\t{file.RelativePath}\t{backupSource}");
        }

        File.WriteAllLines(Path.Combine(backupDir, RawManifestFileName), manifest);
        FxPatchStateStore.MarkApplied(resolved, new FxPatchStateStore.AppliedPatch(
            pack.PatchId, pack.PatchId, FxPatchStateStore.KindRawPack, null, DateTimeOffset.UtcNow, sourceFile));
        FxPatchEngine.Log($"[成功] {pack.PatchId} 应用完成：{pack.Files.Count} 个文件已写入 {indexDir}");
        FxPatchEngine.Log($"       原始文件备份在 {backupDir}，点「还原补丁」即可放回去。");
        return 0;
    }

    /// <summary>还原：按清单把备份的文件放回原位；补丁新增的文件（原版没有）则删除。</summary>
    private static int RawRevert(string resolved, RawPack pack)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            FxPatchEngine.LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再还原补丁。");
            return 1;
        }

        var backupDir = Path.Combine(IndexBackupService.GetBackupDirectory(resolved), pack.PatchId);
        return RevertRawPackFiles(resolved, pack.PatchId, backupDir, pack.Files);
    }

    /// <summary>按账本里的记录还原一个整包替换型补丁——不需要补丁包本身，还原清单
    /// （<c>backup/&lt;补丁名&gt;/manifest.txt</c>）里有全部信息。供「彻底还原游戏客户端」逐个调用；
    /// 清单缺失时告警并返回非 0，不中断其他补丁的还原。</summary>
    internal static int RevertRawPackFromLedger(string resolved, string patchId)
    {
        var backupDir = Path.Combine(IndexBackupService.GetBackupDirectory(resolved), patchId);
        var manifest = ReadRawManifest(Path.Combine(backupDir, RawManifestFileName));
        if (manifest.Count == 0)
        {
            FxPatchEngine.LogErr($"[警告] 整包替换补丁 {patchId} 没有还原清单（{backupDir}\\{RawManifestFileName}），"
                + "无法自动还原它覆盖的文件；可从该目录手动放回，或用启动器的「验证/修复游戏文件」。");
            return 1;
        }
        var files = manifest.Keys
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(rel => new RawPackFile(rel, ""))
            .ToList();
        return RevertRawPackFiles(resolved, patchId, backupDir, files);
    }

    /// <summary>整包替换型还原的核心：逐文件把备份放回原位 / 删除补丁新增文件，随后自检悬空引用。</summary>
    private static int RevertRawPackFiles(string resolved, string patchId, string backupDir, IReadOnlyList<RawPackFile> files)
    {
        var indexDir = Path.GetDirectoryName(resolved)!;
        var baselinePath = IndexBackupService.GetBaselinePath(resolved);
        var manifest = ReadRawManifest(Path.Combine(backupDir, RawManifestFileName));

        foreach (var file in files)
        {
            var target = Path.Combine(indexDir, file.RelativePath);
            manifest.TryGetValue(file.RelativePath, out var entry);

            if (entry?.Kind == "added")
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    FxPatchEngine.Log($"[删除] {file.RelativePath}（补丁新增，原版没有）");
                }
                else
                    FxPatchEngine.Log($"[跳过] {file.RelativePath} 已不存在");
                continue;
            }

            var source = entry?.BackupSource == "baseline"
                ? baselinePath
                : Path.Combine(backupDir, file.RelativePath);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                FxPatchEngine.Log($"[还原] {file.RelativePath} ← {source}");
            }
            else
                FxPatchEngine.LogErr($"[警告] {file.RelativePath} 找不到原始备份，未改动（可从 {backupDir} 手动恢复，或用「还原原版索引」）");
        }

        FxPatchStateStore.MarkRemoved(resolved, patchId);

        // 快照可能早于其他补丁的卸载：还原出的索引也许还引用已被删除的 PATCHED bundle（悬空）。
        // 不修的话，之后任何打开游戏数据的操作（词缀上色连接、游戏读文件）都会直接失败。
        // 就地按基线把悬空引用归位到原版位置，悬空 bundle 由索引孤儿清理移除。
        // 修复是尽力而为：索引打不开（损坏 / 测试夹具的假索引）只告警，不让还原本身报失败。
        try
        {
            var repaired = PatchBundleRepair.RepairIfBroken(resolved, FxPatchEngine.Log);
            if (repaired > 0)
                FxPatchEngine.Log($"[修复] 还原的索引里有 {repaired} 个文件指向已丢失的补丁 bundle，已按原版基线归位。");
        }
        catch (Exception ex)
        {
            FxPatchEngine.LogErr($"[警告] 还原后自检悬空补丁引用未完成：{ex.Message}");
        }

        FxPatchEngine.Log($"[成功] {patchId} 还原完成。");
        return 0;
    }

    private static int RawStatus(string resolved, RawPack pack)
    {
        var indexDir = Path.GetDirectoryName(resolved)!;
        var backupDir = Path.Combine(IndexBackupService.GetBackupDirectory(resolved), pack.PatchId);
        FxPatchEngine.Log($"索引目录: {indexDir}");
        FxPatchEngine.Log($"补丁: {pack.PatchId}（整包替换型，{pack.Files.Count} 个文件）");
        FxPatchEngine.Log($"备份目录: {backupDir}{(Directory.Exists(backupDir) ? "" : "（尚不存在，应用时自动创建）")}");

        var applied = 0;
        foreach (var file in pack.Files)
        {
            var target = Path.Combine(indexDir, file.RelativePath);
            var state = File.Exists(target) && SameContent(target, file.SourcePath) ? "已应用" : "未应用";
            if (state == "已应用")
                applied++;
            var backedUp = File.Exists(Path.Combine(backupDir, file.RelativePath)) ? "已备份" : "无备份";
            FxPatchEngine.Log($"  [{state}] {file.RelativePath}（{backedUp}）");
        }

        FxPatchEngine.Log($"[结果] {applied}/{pack.Files.Count} 个文件已是补丁内容。");
        return 0;
    }

    private static Dictionary<string, (string Kind, string BackupSource)?> ReadRawManifest(string manifestPath)
    {
        var result = new Dictionary<string, (string, string)?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath))
            return result;
        foreach (var line in File.ReadAllLines(manifestPath))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            result[parts[1]] = (parts[0], parts.Length > 2 ? parts[2] : "");
        }
        return result;
    }

    /// <summary>先比大小再比 SHA-256，避免大文件白算哈希。</summary>
    private static bool SameContent(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length)
            return false;
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
}
