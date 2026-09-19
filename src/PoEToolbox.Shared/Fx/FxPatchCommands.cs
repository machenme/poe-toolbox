using System.IO;

namespace PoEToolbox.Shared;

/// <summary>
/// 补丁引擎的命令层：一个子命令一个方法，负责开索引、游戏运行态检查、事务回滚与账本登记。
/// 判定与落盘的细节都不在这里——它只调度 <see cref="FxPatchState"/>（读）、
/// <see cref="FxPatchOperations"/>（写）和 <see cref="FxRawPackPatch"/>（整包替换型）。
/// </summary>
/// <remarks>
/// S8 从 <see cref="FxPatchEngine"/> 原样搬出，除 §2.2 要求的三处取方法外，方法体未改：
/// <c>CmdApplyOrRevert</c>(160) 拆出 <c>Precheck</c> / <c>ApplyWrites</c> / <c>Verify</c>，
/// <c>CmdRestoreBaselineFull</c>(98) 拆出 <c>RevertAllRawPacks</c>。
/// 日志仍汇入引擎那一个出口（下面的私有转发就是为此），否则 UI 的 LogSink 会漏掉命令输出。
/// </remarks>
internal static class FxPatchCommands
{
    private static void Log(string msg) => FxPatchEngine.Log(msg);
    private static void LogErr(string msg) => FxPatchEngine.LogErr(msg);

    internal static int CmdCleanup(string resolved)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再清理补丁 Bundle。");
            return 1;
        }

        var removed = GameDataLoader.Use(resolved, GameDataMode.ReadWrite,
            gd => gd.CleanupOrphanCustomBundles(saveIndex: true));
        Log(removed == 0
            ? "[完成] 没有发现孤儿补丁 Bundle。"
            : $"[完成] 已清理 {removed} 个无引用补丁 Bundle。");
        return 0;
    }

    /// <summary>彻底恢复官方原版：先按各补丁的备份清单把「整包替换型补丁」覆盖的原生文件放回去
    /// （不需要补丁包本身，清单里有全部信息），再用 backup\_.index.bin 基线整体替换当前索引，
    /// 最后删掉 PATCHED 目录里补丁新增的 bundle 物理文件（基线索引不再引用它们）。账本随基线恢复一并清空。</summary>
    internal static int CmdRestoreBaselineFull(string resolved)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再恢复原版。");
            return 1;
        }

        RevertAllRawPacks(resolved);

        var indexDir = Path.GetDirectoryName(resolved)!;
        var patchedDir = Path.Combine(indexDir, "PATCHED");

        var baselineMissing = false;
        GameDataLoader.Use(resolved, GameDataMode.ReadWrite, gd =>
        {
            // 没有基线就整体回写不了索引；此时删 PATCHED 文件只会制造悬空引用。
            // 明确中止并指向启动器验证（那边能拿到官方索引），比抛英文 FileNotFoundException 有用得多。
            if (!File.Exists(IndexBackupService.GetBaselinePath(resolved)))
            {
                LogErr("[中止] 没有原始索引备份（Bundles2\\backup\\_.index.bin 缺失），无法整体恢复原版。"
                    + "请用启动器的「验证/修复游戏文件」恢复官方索引；得到干净索引后本工具会自动重建基线。");
                baselineMissing = true;
                return;
            }
            IndexBackupService.RestoreBaseline(gd); // 写回基线索引并清空账本
        });
        if (baselineMissing)
            return 1;

        var removed = 0;
        if (Directory.Exists(patchedDir))
        {
            foreach (var bundle in Directory.EnumerateFiles(patchedDir, "*.bundle.bin"))
            {
                try
                {
                    File.Delete(bundle);
                    removed++;
                }
                catch (Exception ex)
                {
                    LogErr($"[警告] 无法删除 {Path.GetFileName(bundle)}：{ex.Message}（不影响游戏，之后可手动删除）");
                }
            }
            Log(removed == 0
                ? "[清理] PATCHED 目录没有需要删除的补丁文件。"
                : $"[清理] 已删除 {removed} 个补丁新增的 bundle 文件。");
            try
            {
                if (!Directory.EnumerateFileSystemEntries(patchedDir).Any())
                    Directory.Delete(patchedDir);
            }
            catch (Exception ex)
            {
                LogErr($"[警告] PATCHED 目录清理失败：{ex.Message}");
            }
        }

        Log("[完成] 已恢复原版索引；所有补丁记录已清空，想再用需要重新启用补丁。");

        // 基线可能过期或被污染（在补丁应用状态下创建/刷新），恢复出的索引也许仍引用已删除/丢失的
        // PATCHED bundle。不修的话，之后打开游戏数据乃至游戏加载都会直接失败。
        // 尽力而为：与整包还原（RawRevert）同一口径；修不动时 RepairIfBroken 的日志已说明成因与出路。
        try
        {
            var repaired = PatchBundleRepair.RepairIfBroken(resolved, Log);
            if (repaired > 0)
                Log($"[修复] 恢复出的索引仍引用已丢失的补丁 bundle，已把 {repaired} 个文件归位到原版位置。");
        }
        catch (Exception ex)
        {
            LogErr($"[警告] 恢复后自检悬空补丁引用未完成：{ex.Message}");
        }
        return 0;
    }

    /// <summary>整包替换型补丁覆盖的是游戏原生文件（含索引本体），还原只靠备份清单，不依赖基线也不开索引；
    /// 必须在基线回写之前做：其中索引的备份来源可能是基线（backupSource=baseline）。</summary>
    private static void RevertAllRawPacks(string resolved)
    {
        var rawPackIds = FxPatchStateStore.Read(resolved)
            .Where(e => string.Equals(e.Kind, FxPatchStateStore.KindRawPack, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .ToList();
        if (rawPackIds.Count == 0)
            return;

        Log($"[提示] 检测到 {rawPackIds.Count} 个整包替换型补丁（{string.Join("、", rawPackIds)}），将按备份清单把它们覆盖的原生文件一并还原。");

        var rawPackFailures = 0;
        foreach (var id in rawPackIds)
        {
            if (FxRawPackPatch.RevertRawPackFromLedger(resolved, id) != 0)
                rawPackFailures++;
        }
        if (rawPackFailures > 0)
            LogErr($"[警告] {rawPackFailures} 个整包替换型补丁没能完整还原（缺备份清单或缺备份文件），详见上方日志。");
        else
            Log($"[完成] {rawPackIds.Count} 个整包替换型补丁覆盖的原生文件已全部还原。");
    }

    internal static int CmdPurge(string resolved, PatchDef patch)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再彻底清理补丁文件。");
            return 1;
        }

        var owned = FxPatchIdentity.OwnedPathsOf(patch);
        if (owned.Count == 0)
        {
            Log("[完成] 本补丁没有新增文件（只修改了游戏原有文件），无需彻底清理——还原补丁即可。");
            return 0;
        }

        var versionLabel = string.IsNullOrWhiteSpace(patch.Version) ? "" : $" v{patch.Version}";
        Log($"[清理] {patch.PatchId}{versionLabel}：只移除本补丁新增的 {owned.Count} 个路径，游戏原有文件一律保留。");
        // 一次 Use 内做完「删补丁文件 + 清孤儿 bundle」，只落盘一次。
        // 孤儿收尾原本是独立的 cleanup 命令；捆绑在这里后，用户卸载完补丁不需要再手动跑一次，
        // 语义完全相同——只删索引里已经没有任何文件记录的 PATCHED bundle（跨补丁，含本补丁腾空后的残留）。
        var removed = 0;
        var orphansRemoved = 0;
        GameDataLoader.Use(resolved, GameDataMode.ReadWrite, gd =>
        {
            removed = gd.PurgePatchBundles(patch.BundleName, owned, saveIndex: false);
            orphansRemoved = gd.CleanupOrphanCustomBundles(saveIndex: false);
            // 必须自己收尾落盘：CleanupOrphanCustomBundles 只有发现空 bundle 时才 Save
            // （Index.cs:135-137），而 purge 后 bundle 里往往还留着被 Redirect 进来的原生文件、
            // 并不为空 —— 那次 Save 不会发生，purge 的改动就全丢了。
            // 而 Save 本身（Index.cs:549-584）就会先摘掉空 bundle、写完索引再删它们的物理文件，
            // 所以一次 gd.Save() 同时提交了两件事。
            if (removed != 0 || orphansRemoved != 0)
                gd.Save();
        });
        RecordRemoved(resolved, patch);

        Log($"[完成] 已清理 {removed} 个补丁新增文件；索引中仍被引用的游戏文件保持不动。");
        Log(orphansRemoved == 0
            ? "[收尾] 没有残留的空补丁 Bundle 需要清理。"
            : $"[收尾] 顺带清理了 {orphansRemoved} 个已无引用的补丁 Bundle。");
        return 0;
    }

    // ═══ status ═════════════════════════════════════════════════
    /// <summary>列出全部内置补丁的安装状态：☑ 已启用、☐ 未启用、⚠ 部分应用或冲突。</summary>
    internal static int CmdStatusList(string resolved)
    {
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            Log("内置补丁状态：");
            foreach (var def in FxBuiltInPatches.BuiltIns)
            {
                PatchDef patch;
                try
                {
                    patch = FxBuiltInPatches.LoadBuiltInPatch(def);
                }
                catch (Exception ex)
                {
                    Log($"  ⚠ {def.DisplayName}（{def.Id}）——补丁描述加载失败：{ex.Message}");
                    continue;
                }
                Log($"  {FxPatchState.MarkOf(FxPatchState.ComputeStates(gd, patch))} {def.DisplayName}（{def.Id}）");
            }
            return 0;
        }
    }

    internal static int CmdStatus(string resolved, PatchDef patch)
    {
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            Log($"索引: {gd.GameDataPath}");
            Log($"补丁: {patch.PatchId}");
            foreach (var s in FxPatchState.ComputeStates(gd, patch))
                Log($"  [{FxPatchState.Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");
            return 0;
        }
    }

    // ═══ apply / revert ═════════════════════════════════════════
    internal static int CmdApplyOrRevert(string resolved, PatchDef patch, bool apply, string? sourceFile = null)
    {
        var verb = apply ? "应用" : "还原";

        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再执行补丁应用或还原；背包清理使用独立的运行态流程。");
            return 1;
        }

        if (Precheck(resolved, patch, apply, out var unresolvedBefore) != 0)
            return 1;

        // 预检期间游戏可能被重新启动，写入前再检查一次。
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] 检测到 Path of Exile 已启动，未做任何写入。");
            return 1;
        }

        var exit = ApplyWrites(resolved, patch, apply);
        if (exit != 0)
            return exit;

        var verify = Verify(resolved, patch, apply, unresolvedBefore);
        if (verify != 0)
            return verify;

        // 到账这一步才算真的生效：写账本失败只提示，不影响补丁结果。
        if (apply)
            RecordApplied(resolved, patch, sourceFile);
        else
            RecordRemoved(resolved, patch);

        Log($"[成功] {patch.PatchId} {verb}完成。");
        return 0;
    }

    /// <summary>Pass 1：只读预检，冲突/不兼容在写入前中止。返回非 0 即调用方必须中止。</summary>
    private static int Precheck(string resolved, PatchDef patch, bool apply, out int unresolvedBefore)
    {
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            unresolvedBefore = FxPatchState.CountUnresolved(gd);
            Log($"[预检] {patch.PatchId} → {(apply ? "apply" : "revert")}");
            var preview = FxPatchState.ComputeStates(gd, patch, apply);
            foreach (var s in preview)
                Log($"  [{FxPatchState.Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");
            if (preview.Any(s => s.State is PatchState.Conflict or PatchState.Incompatible))
            {
                LogErr("[中止] 存在冲突或不兼容操作，未做任何写入。");
                // 词缀补丁由「词缀上色」页按当时的游戏文件现生成；这里启用的 json 是上一次的产物，
                // 游戏文件变过（应用/卸载其他补丁、换汉化、游戏更新）就必然对不上。
                if (string.Equals(patch.PatchId, AffixPatchBuilder.PatchId, StringComparison.OrdinalIgnoreCase))
                    LogErr("[指引] 这是词缀上色生成的补丁，与当前游戏文件内容不一致（通常是之后应用或卸载过其他补丁，文本已变化）。"
                        + "请到「词缀上色」页处理：要用就重新点「应用词缀修改」，不用了就在那里恢复原版。");
                return 1;
            }
        }
        return 0;
    }

    /// <summary>Pass 2：写入。独立 bundle，事务失败要退回写入前的索引字节。</summary>
    private static int ApplyWrites(string resolved, PatchDef patch, bool apply)
    {
        // 独立 bundle：PATCHED/<bundleName>_v<version>.bundle.bin，后缀由库追加
        var bundlePath = FxPatchIdentity.BundlePathOf(patch);
        return GameDataLoader.Use(resolved, GameDataMode.ReadWrite, gd =>
        {
            gd.PinnedWriteBundlePath = bundlePath;
            Log($"[写入] 补丁 bundle: {bundlePath}");

            var backup = IndexBackupService.Begin(gd);
            var beforeIndexBytes = gd.ReadIndexBytes();
            var mutationSnapshot = gd.Index.CaptureMutationSnapshot();
            var originalMaxBundleSize = gd.Index.MaxBundleSize;
            var changed = false;
            var committed = false;
            try
            {
                // 补丁事务不能在中途触发 Bundle flush；所有 FileRecord/AddFile 操作
                // 都只改内存中的索引和单个待写 Bundle，最后统一 Save 一次。
                gd.Index.MaxBundleSize = int.MaxValue;
                foreach (var op in patch.Operations)
                {
                    var opChanged = FxPatchOperations.ExecuteOp(gd, patch, op, apply);
                    changed |= opChanged;
                }

                if (changed)
                {
                    gd.Save();
                    committed = true;
                }
                else
                    Log("[完成] 无需写入（全部操作已处于目标状态）。");

                // 审计日志不是游戏数据事务的一部分；保存成功后即使日志目录不可写，
                // 也不能把已经提交的游戏数据误报成失败。
                try
                {
                    IndexBackupService.Complete(gd, backup, apply ? "fx-patch-apply" : "fx-patch-revert",
                        new Dictionary<string, string>
                        {
                            ["patchId"] = patch.PatchId,
                            ["bundle"] = bundlePath,
                        });
                    Log($"[备份] baseline={backup.BaselinePath}");
                }
                catch (Exception ex)
                {
                    LogErr($"[警告] 游戏数据已保存，但写入审计日志失败：{ex.Message}");
                }
                return 0;
            }
            catch
            {
                if (!committed)
                {
                    try
                    {
                        // 尚未 Save 时，唯一可能落盘的是新建/刷出的本次补丁 Bundle。
                        // 回滚它即可保证原索引和原 Bundle 不受影响；当前 gd 随后会被释放。
                        gd.Index.RollbackMutation(mutationSnapshot);
                        try
                        {
                            gd.WriteIndexBytes(beforeIndexBytes);
                        }
                        catch (Exception restoreEx)
                        {
                            LogErr($"[严重] 原始索引恢复失败：{restoreEx.Message}");
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        LogErr($"[严重] 补丁事务失败，且清理临时 Bundle 失败：{rollbackEx.Message}");
                    }
                }
                throw;
            }
            finally
            {
                gd.Index.MaxBundleSize = originalMaxBundleSize;
            }
        });
    }

    /// <summary>Pass 3：重开索引校验，确认写入真的按预期落到了盘上。返回非 0 表示校验未过。</summary>
    private static int Verify(string resolved, PatchDef patch, bool apply, int unresolvedBefore)
    {
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            var after = FxPatchState.ComputeStates(gd, patch);
            Log("[校验] 重新打开索引后的状态：");
            foreach (var s in after)
                Log($"  [{FxPatchState.Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");

            var targetState = apply ? PatchState.Applied : PatchState.NotApplied;
            // revert 豁免：addfile-derived 副本按设计保留；addfile-asset 无原版字节（新增类）同样保留
            var allOk = after.All(s =>
                !apply && (s.Op.Op == "addfile-derived" || (s.Op.Op == "addfile-asset" && s.Op.OriginalAsset is null))
                    || s.State == targetState);
            var unresolvedAfter = FxPatchState.CountUnresolved(gd);
            var unresolvedOk = unresolvedAfter == unresolvedBefore;
            Log($"[校验] unresolved: {unresolvedBefore:N0} -> {unresolvedAfter:N0} {(unresolvedOk ? "OK" : "MISMATCH!")}");
            foreach (var s in after.Where(s => !apply && s.State == PatchState.Applied
                && (s.Op.Op == "addfile-derived" || (s.Op.Op == "addfile-asset" && s.Op.OriginalAsset is null))))
                Log($"[校验] {s.Op.Dst} 保留（新增内容，无引用/无还原目标时无害）");
            if (!allOk)
            {
                LogErr($"[失败] 校验未全部达到{(apply ? "已应用" : "未应用")}状态。");
                return 1;
            }
            if (!unresolvedOk)
                return 1;
        }
        return 0;
    }

    /// <summary>登记一个已应用的补丁（界面靠这份账本显示「打了哪些」，不必重开索引）。</summary>
    private static void RecordApplied(string resolved, PatchDef patch, string? sourceFile = null)
        => FxPatchStateStore.MarkApplied(resolved, new FxPatchStateStore.AppliedPatch(
            patch.PatchId, DisplayNameOf(patch), KindOf(patch), FxPatchIdentity.BundlePathOf(patch), DateTimeOffset.UtcNow, sourceFile));

    /// <summary>撤销一个补丁的登记（还原 / 卸载后调用）。</summary>
    private static void RecordRemoved(string resolved, PatchDef patch)
        => FxPatchStateStore.MarkRemoved(resolved, patch.PatchId);

    /// <summary>内置补丁用注册表里的中文名，自定义补丁没有名字就用 id。</summary>
    private static string DisplayNameOf(PatchDef patch)
        => FxBuiltInPatches.BuiltIns.FirstOrDefault(b => b.Id.Equals(patch.PatchId, StringComparison.OrdinalIgnoreCase))?.DisplayName
           ?? patch.PatchId;

    private static string KindOf(PatchDef patch)
        => FxBuiltInPatches.BuiltIns.Any(b => b.Id.Equals(patch.PatchId, StringComparison.OrdinalIgnoreCase))
            ? FxPatchStateStore.KindBuiltIn
            : FxPatchStateStore.KindCustom;
}
