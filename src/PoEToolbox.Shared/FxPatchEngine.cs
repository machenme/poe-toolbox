using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoEToolbox.Shared;
using System.IO;

/// <summary>
/// 通用特效补丁引擎（fx-patch 范式）。
/// 补丁 = 一个声明式 .patch.json（记录对逻辑文件的修改 + 目标 bundle 名），
/// 引擎按描述就地修改索引：幂等、三态判定、冲突中止、独立 PATCHED bundle、自动备份。
/// fx-oilmod = 引擎内置的第一个补丁实例（oil-grenade-fx-lite）。
/// CLI 直接调用 Run()；UI 设置 <see cref="LogSink"/> 后调用即可捕获全部输出。
/// </summary>
public static class FxPatchEngine
{
    /// <summary>UI 日志钩子：设置后所有引擎输出同时流入该回调（CLI 不设置，走 Console）。</summary>
    public static Action<string>? LogSink;

    private static void Log(string msg)
    {
        LogSink?.Invoke(msg);
        Console.WriteLine(msg);
        FileLogger.App.Info($"[fx-patch] {msg}");
    }

    private static void LogErr(string msg)
    {
        LogSink?.Invoke("[错误] " + msg);
        Console.Error.WriteLine(msg);
        FileLogger.App.Error($"[fx-patch] {msg}");
    }

    private const string BuiltInPatchFileName = "oil-grenade-fx-lite.patch.json";

    // ═══ 补丁描述模型 ═══════════════════════════════════════════
    internal sealed class PatchDef
    {
        public string PatchId { get; set; } = "";
        public string BundleName { get; set; } = "";
        public List<PatchOp> Operations { get; set; } = new();

        /// <summary>补丁描述文件所在目录（assets 相对路径的基准）。运行时注入，不序列化。</summary>
        [JsonIgnore]
        public string? BasePath { get; set; }
    }

    internal sealed class PatchOp
    {
        public string Op { get; set; } = "";       // addfile-derived | addfile-asset | patchptr-byid | edittext
        public string? Src { get; set; }           // addfile-derived
        public string? Dst { get; set; }
        public List<TextReplace> Replace { get; set; } = new();
        public string? Asset { get; set; }         // addfile-asset：补丁包内成品内容（相对 BasePath）
        public string? OriginalAsset { get; set; } // addfile-asset：原版内容（可选，供 revert 还原）
        public string? Table { get; set; }         // patchptr-byid
        public string? Id { get; set; }
        public string? OriginalPath { get; set; }
        public string? NewPath { get; set; }
        public string? Path { get; set; }          // edittext
        public string? Old { get; set; }
        public string? New { get; set; }

        [JsonIgnore]
        public string Describe => Op switch
        {
            "addfile-derived" => $"副本+替换  {Src} → {Dst}",
            "addfile-asset" => $"成品内容  {Dst}" + (OriginalAsset is null ? "（新增）" : "（替换）"),
            "patchptr-byid" => $"指针重定向  {Table} 中 Id={Id}",
            "edittext" => $"文本替换  {Path}",
            _ => Op,
        };
    }

    internal sealed class TextReplace
    {
        public string Old { get; set; } = "";
        public string New { get; set; } = "";
        public int Count { get; set; } = -1;       // -1 = 全部替换
    }

    internal enum PatchState { NotApplied, Applied, Conflict, Incompatible }

    private sealed record OpState(PatchOp Op, PatchState State, string Detail);

    // ═══ 入口 ═══════════════════════════════════════════════════
    public static int Run(string[] args, bool builtInPatch)
    {
        // fx-oilmod <game-data> <status|apply|revert>
        // fx-patch <game-data> <patch.json> <status|apply|revert>
        string gameData, action;
        string? patchPath = null;
        if (builtInPatch)
        {
            if (args.Length != 2) { Usage(builtInPatch); return 2; }
            gameData = args[0];
            action = args[1];
        }
        else
        {
            // fx-patch diff <vanilla> <modified> [-o dir] [--id x] [--bundle y]
            if (args.Length > 0 && args[0].Equals("diff", StringComparison.OrdinalIgnoreCase))
                return FxDiff.Run(args[1..]);
            if (args.Length != 3) { Usage(builtInPatch); return 2; }
            gameData = args[0];
            patchPath = args[1];
            action = args[2];
        }

        action = action.ToLowerInvariant();
        if (action is not ("status" or "apply" or "revert")) { Usage(builtInPatch); return 2; }

        PatchDef patch;
        try
        {
            var path = builtInPatch
                ? ResolveBuiltInPatchPath()
                : patchPath!;
            if (!File.Exists(path))
            {
                LogErr($"补丁描述文件不存在: {path}");
                return 1;
            }
            // 支持分发为 zip 压缩包（目录压缩而成）：解压到临时目录后按普通补丁处理
            if (!builtInPatch && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extracted = ExtractZipPatch(path);
                if (extracted is null)
                    return 1;
                path = extracted;
            }
            patch = JsonSerializer.Deserialize<PatchDef>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? throw new InvalidOperationException("补丁描述解析为空。");
            patch.BasePath = Path.GetDirectoryName(Path.GetFullPath(path));
            ValidatePatch(patch);
        }
        catch (Exception ex)
        {
            LogErr($"补丁描述加载失败: {ex.Message}");
            return 1;
        }

        try
        {
            var resolved = GameDataLoader.ResolvePath(gameData);
            return action switch
            {
                "status" => CmdStatus(resolved, patch),
                "apply" => CmdApplyOrRevert(resolved, patch, apply: true),
                _ => CmdApplyOrRevert(resolved, patch, apply: false),
            };
        }
        catch (Exception ex)
        {
            LogErr($"Error: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveBuiltInPatchPath()
    {
        // 部署形态兼容：exe 同目录或 Patches\ 子目录（csproj PreserveNewest 复制会保留目录结构）
        var sub = Path.Combine(AppContext.BaseDirectory, "Patches", BuiltInPatchFileName);
        if (File.Exists(sub))
            return sub;
        return Path.Combine(AppContext.BaseDirectory, BuiltInPatchFileName);
    }

    /// <summary>
    /// 把 zip 补丁包解压到临时目录并返回其中 patch.json 的路径。
    /// 兼容两种打包方式：压缩整个目录（顶层有一层文件夹）或压缩目录内容（patch.json 在 zip 根）。
    /// 临时目录按天清理，避免残留。
    /// </summary>
    private static string? ExtractZipPatch(string zipPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var jsonEntry = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Where(e => Path.GetFileName(e.FullName).Equals("patch.json", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(e.FullName).EndsWith(".patch.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Replace('\\', '/').Count(c => c == '/'))
                .FirstOrDefault();
            if (jsonEntry is null)
            {
                LogErr($"压缩包内未找到补丁描述（patch.json / *.patch.json）: {zipPath}");
                return null;
            }

            var fullName = jsonEntry.FullName.Replace('\\', '/');
            var slash = fullName.LastIndexOf('/');
            var prefix = slash >= 0 ? fullName[..(slash + 1)] : "";

            var baseDir = Path.Combine(Path.GetTempPath(), "poe-toolbox-patch-" + Guid.NewGuid().ToString("N")[..12]);
            CleanStalePatchTempDirs(baseDir);

            foreach (var entry in archive.Entries)
            {
                var entryName = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = entryName[prefix.Length..];
                if (relative.Length == 0)
                    continue;
                var destination = Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (entryName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open();
                using var target = File.Create(destination);
                source.CopyTo(target);
            }

            Log($"已解压补丁包: {Path.GetFileName(zipPath)} → {baseDir}");
            return Path.Combine(baseDir, fullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception ex)
        {
            LogErr($"解压补丁包失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>清理超过 24 小时的旧补丁解压临时目录。</summary>
    private static void CleanStalePatchTempDirs(string currentDir)
    {
        try
        {
            var root = Path.GetDirectoryName(currentDir)!;
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var dir in Directory.EnumerateDirectories(root, "poe-toolbox-patch-*"))
            {
                try { if (dir != currentDir && Directory.GetLastWriteTime(dir) < cutoff) Directory.Delete(dir, recursive: true); }
                catch { }
            }
        }
        catch { }
    }

    private static void Usage(bool builtInPatch)
    {
        LogErr(builtInPatch
            ? "Usage: fx-oilmod <game-data> <status|apply|revert>"
            : "Usage: fx-patch <game-data> <patch.json|patch.zip> <status|apply|revert>\n       fx-patch diff <原版index.bin> <修改后index.bin> [-o 目录] [--id x] [--bundle y]");
    }

    private static void ValidatePatch(PatchDef patch)
    {
        if (string.IsNullOrWhiteSpace(patch.PatchId)) throw new InvalidOperationException("缺少 patchId。");
        if (string.IsNullOrWhiteSpace(patch.BundleName)) throw new InvalidOperationException("缺少 bundleName。");
        if (patch.Operations.Count == 0) throw new InvalidOperationException("operations 为空。");
        foreach (var op in patch.Operations)
        {
            switch (op.Op)
            {
                case "addfile-derived" when string.IsNullOrEmpty(op.Src) || string.IsNullOrEmpty(op.Dst):
                    throw new InvalidOperationException("addfile-derived 需要 src/dst。");
                case "addfile-asset" when string.IsNullOrEmpty(op.Dst) || string.IsNullOrEmpty(op.Asset):
                    throw new InvalidOperationException("addfile-asset 需要 dst/asset。");
                case "patchptr-byid" when string.IsNullOrEmpty(op.Table) || string.IsNullOrEmpty(op.Id)
                    || string.IsNullOrEmpty(op.OriginalPath) || string.IsNullOrEmpty(op.NewPath):
                    throw new InvalidOperationException("patchptr-byid 需要 table/id/originalPath/newPath。");
                case "edittext" when string.IsNullOrEmpty(op.Path) || op.Old is null || op.New is null:
                    throw new InvalidOperationException("edittext 需要 path/old/new。");
            }
        }
    }

    // ═══ status ═════════════════════════════════════════════════
    private static int CmdStatus(string resolved, PatchDef patch)
    {
        using var gd = GameDataAccess.OpenReadOnlyMapped(resolved);
        Log($"索引: {gd.GameDataPath}");
        Log($"补丁: {patch.PatchId}");
        foreach (var s in ComputeStates(gd, patch))
            Log($"  [{Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");
        return 0;
    }

    // ═══ apply / revert ═════════════════════════════════════════
    private static int CmdApplyOrRevert(string resolved, PatchDef patch, bool apply)
    {
        var verb = apply ? "应用" : "还原";

        // Pass 1：只读预检，冲突/不兼容在写入前中止
        int unresolvedBefore;
        OpState[] preview;
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            unresolvedBefore = CountUnresolved(gd);
            Log($"[预检] {patch.PatchId} → {(apply ? "apply" : "revert")}");
            preview = ComputeStates(gd, patch);
            foreach (var s in preview)
                Log($"  [{Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");
            if (preview.Any(s => s.State is PatchState.Conflict or PatchState.Incompatible))
            {
                LogErr("[中止] 存在冲突或不兼容操作，未做任何写入。");
                return 1;
            }
        }

        // Pass 2：写入（独立 bundle：PATCHED/<bundleName>_<时间戳>.bundle.bin，后缀由库追加）
        var bundlePath = $"PATCHED/{patch.BundleName}_{DateTime.Now:yyyyMMdd-HHmmss}";
        var exit = GameDataLoader.Use(resolved, GameDataMode.ReadWrite, gd =>
        {
            gd.PinnedWriteBundlePath = bundlePath;
            Log($"[写入] 补丁 bundle: {bundlePath}");

            var backup = IndexBackupService.Begin(gd);
            var changed = false;
            foreach (var op in patch.Operations)
            {
                var opChanged = ExecuteOp(gd, patch, op, apply);
                changed |= opChanged;
            }

            if (changed)
                gd.Save();
            else
                Log("[完成] 无需写入（全部操作已处于目标状态）。");

            IndexBackupService.Complete(gd, backup, apply ? "fx-patch-apply" : "fx-patch-revert",
                new Dictionary<string, string>
                {
                    ["patchId"] = patch.PatchId,
                    ["bundle"] = bundlePath,
                });
            Log($"[备份] baseline={backup.BaselinePath}");
            return 0;
        });
        if (exit != 0)
            return exit;

        // Pass 3：重开校验
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            var after = ComputeStates(gd, patch);
            Log("[校验] 重新打开索引后的状态：");
            foreach (var s in after)
                Log($"  [{Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");

            var targetState = apply ? PatchState.Applied : PatchState.NotApplied;
            // revert 豁免：addfile-derived 副本按设计保留；addfile-asset 无原版字节（新增类）同样保留
            var allOk = after.All(s =>
                !apply && (s.Op.Op == "addfile-derived" || (s.Op.Op == "addfile-asset" && s.Op.OriginalAsset is null))
                    || s.State == targetState);
            var unresolvedAfter = CountUnresolved(gd);
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

        Log($"[成功] {patch.PatchId} {verb}完成。");
        return 0;
    }

    // ═══ 单 op 三态计算（只读）══════════════════════════════════
    private static OpState[] ComputeStates(GameDataAccess gd, PatchDef patch)
        => patch.Operations.Select(op => ComputeState(gd, patch, op)).ToArray();

    private static OpState ComputeState(GameDataAccess gd, PatchDef patch, PatchOp op)
    {
        try
        {
            switch (op.Op)
            {
                case "addfile-derived":
                {
                    if (!gd.FileExists(op.Src!))
                        return new OpState(op, PatchState.Incompatible, $"找不到源文件 {op.Src}");
                    return gd.FileExists(op.Dst!)
                        ? new OpState(op, PatchState.Applied, $"副本已存在 {op.Dst}")
                        : new OpState(op, PatchState.NotApplied, "副本不存在");
                }
                case "addfile-asset":
                {
                    var assetPath = Path.Combine(patch.BasePath ?? ".", op.Asset!);
                    if (!File.Exists(assetPath))
                        return new OpState(op, PatchState.Incompatible, $"补丁包内找不到 asset {op.Asset}");
                    if (!gd.FileExists(op.Dst!))
                        return new OpState(op, PatchState.NotApplied, "目标文件不存在");
                    var sha = Sha(gd.ReadFile(op.Dst!)!);
                    if (sha == Sha(File.ReadAllBytes(assetPath)))
                        return new OpState(op, PatchState.Applied, "内容与补丁 asset 一致");
                    if (op.OriginalAsset is not null)
                    {
                        var origPath = Path.Combine(patch.BasePath ?? ".", op.OriginalAsset);
                        if (File.Exists(origPath) && sha == Sha(File.ReadAllBytes(origPath)))
                            return new OpState(op, PatchState.NotApplied, "内容为原版");
                    }
                    return new OpState(op, PatchState.Conflict, "内容与补丁/原版均不一致（可能与其他 mod 冲突）");
                }
                case "patchptr-byid":
                {
                    if (!gd.FileExists(op.Table!))
                        return new OpState(op, PatchState.Incompatible, $"找不到表 {op.Table}");
                    var dat = gd.ReadFile(op.Table!)!;
                    var layout = ParseLayout(dat);
                    var field = LocatePtrField(dat, layout, op.Id!, op.OriginalPath!, op.NewPath!);
                    return field is null
                        ? new OpState(op, PatchState.Incompatible, $"表中找不到 Id={op.Id} 的行或字段")
                        : new OpState(op, field.State, $"ROW[{field.Row}] +{field.FieldOffset} = \"{field.CurrentValue}\"");
                }
                case "edittext":
                {
                    if (!gd.FileExists(op.Path!))
                        return new OpState(op, PatchState.Incompatible, $"找不到文件 {op.Path}");
                    var text = DecodeText(gd.ReadFile(op.Path!)!, out _, out _);
                    var hasOld = text.Contains(op.Old!, StringComparison.Ordinal);
                    // 空 New（删除型替换）对 Contains 恒为 true，必须排除
                    var hasNew = op.New!.Length > 0 && text.Contains(op.New!, StringComparison.Ordinal);
                    return (hasOld, hasNew) switch
                    {
                        (true, false) => new OpState(op, PatchState.NotApplied, "原文存在"),
                        (false, true) => new OpState(op, PatchState.Applied, "目标文本存在"),
                        // 删除型替换：New 为空时"原文已消失"即视为已应用
                        (false, false) when op.New.Length == 0 => new OpState(op, PatchState.Applied, "原文已删除"),
                        _ => new OpState(op, PatchState.Conflict, "新旧文本同时缺失/存在（可能与其他 mod 冲突）"),
                    };
                }
                default:
                    return new OpState(op, PatchState.Incompatible, $"未知 op 类型 {op.Op}");
            }
        }
        catch (Exception ex)
        {
            return new OpState(op, PatchState.Incompatible, ex.Message);
        }
    }

    // ═══ 单 op 执行（写入通道内）════════════════════════════════
    private static bool ExecuteOp(GameDataAccess gd, PatchDef patch, PatchOp op, bool apply)
    {
        switch (op.Op)
        {
            case "addfile-asset":
            {
                var assetPath = Path.Combine(patch.BasePath ?? ".", op.Asset!);
                var newBytes = File.ReadAllBytes(assetPath);
                if (apply)
                {
                    if (gd.FileExists(op.Dst!))
                    {
                        if (Sha(gd.ReadFile(op.Dst!)!) == Sha(newBytes))
                        {
                            Log($"[skip] {op.Dst} 内容已与 asset 一致");
                            return false;
                        }
                        gd.Index.TryGetFile(op.Dst!, out var fr);
                        fr!.Write(newBytes);
                        Log($"[op] 替换 {op.Dst}（{newBytes.Length:N0} B，asset）");
                        return true;
                    }
                    gd.AddFile(op.Dst!, newBytes);
                    Log($"[op] 新增 {op.Dst}（{newBytes.Length:N0} B，asset）");
                    return true;
                }
                // revert：有原版字节才还原；新增类（无 OriginalAsset）保留
                if (op.OriginalAsset is null)
                {
                    Log($"[skip] {op.Dst} 保留（新增内容，无原版字节可还原）");
                    return false;
                }
                var origPath = Path.Combine(patch.BasePath ?? ".", op.OriginalAsset);
                if (!File.Exists(origPath))
                    throw new InvalidOperationException($"补丁包内找不到原版 asset {op.OriginalAsset}");
                var origBytes = File.ReadAllBytes(origPath);
                if (!gd.FileExists(op.Dst!) || Sha(gd.ReadFile(op.Dst!)!) == Sha(origBytes))
                {
                    Log($"[skip] {op.Dst} 已是原版内容");
                    return false;
                }
                gd.Index.TryGetFile(op.Dst!, out var fr2);
                fr2!.Write(origBytes);
                Log($"[op] 还原 {op.Dst}（{origBytes.Length:N0} B，原版 asset）");
                return true;
            }
            case "addfile-derived":
            {
                // revert：副本保留（无引用即无害；可整目录清理 PATCHED bundle）
                if (!apply)
                {
                    Log($"[skip] {op.Dst} 副本保留（还原指针后无引用，随 PATCHED bundle 清理）");
                    return false;
                }
                var content = BuildDerivedContent(gd, op);
                if (gd.FileExists(op.Dst!))
                {
                    var existing = DecodeText(gd.ReadFile(op.Dst!)!, out _, out _);
                    var ok = op.Replace.All(r => existing.Contains(r.New, StringComparison.Ordinal));
                    if (ok)
                    {
                        Log($"[skip] {op.Dst} 已存在且内容正确");
                        return false;
                    }
                    gd.Index.TryGetFile(op.Dst!, out var fr);
                    fr!.Write(content);
                    Log($"[op] 重写副本 {op.Dst}（{content.Length:N0} B）");
                    return true;
                }
                gd.AddFile(op.Dst!, content);
                Log($"[op] 新增副本 {op.Dst}（{content.Length:N0} B）");
                return true;
            }
            case "patchptr-byid":
            {
                var dat = gd.ReadFile(op.Table!)!;
                var layout = ParseLayout(dat);
                var field = LocatePtrField(dat, layout, op.Id!, op.OriginalPath!, op.NewPath!)
                    ?? throw new InvalidOperationException($"表中找不到 Id={op.Id}（客户端不兼容）");
                var target = apply ? op.NewPath! : op.OriginalPath!;
                // apply 目标是补丁专属新路径 → 开启单引用约束（哨兵劫持修复）；revert 回共享原始路径 → 必须关闭
                var (newDat, changed) = RepointString(dat, layout, field, target, fixForeignRefs: apply);
                if (!changed)
                {
                    Log($"[skip] 指针已指向 \"{target}\"");
                    return false;
                }
                gd.Index.TryGetFile(op.Table!, out var fr);
                fr!.Write(newDat);
                Log($"[op] {op.Table} ROW[{field.Row}] +{field.FieldOffset} → \"{target}\"");
                return true;
            }
            case "edittext":
            {
                var from = apply ? op.Old! : op.New!;
                var to = apply ? op.New! : op.Old!;
                if (from.Length == 0)
                {
                    Log($"[skip] {op.Path} 删除型替换无法反向表达，跳过还原");
                    return false;
                }
                var bytes = gd.ReadFile(op.Path!)!;
                var text = DecodeText(bytes, out var enc, out var preamble);
                var count = CountOccurrences(text, from);
                if (count == 0)
                {
                    Log($"[skip] {op.Path} 已是目标状态");
                    return false;
                }
                if (count != 1)
                    throw new InvalidOperationException($"{op.Path} 中目标文本出现 {count} 次（预期 1 次），拒绝硬改。");
                text = text.Replace(from, to, StringComparison.Ordinal);
                gd.Index.TryGetFile(op.Path!, out var fr);
                fr!.Write(EncodeText(text, enc, preamble));
                Log($"[op] {op.Path} 文本已替换");
                return true;
            }
            default:
                throw new InvalidOperationException($"未知 op 类型 {op.Op}");
        }
    }

    // ═══ addfile-derived：读原件 → 声明的替换 → 回编码（动态生成，不内嵌成品）═══
    private static byte[] BuildDerivedContent(GameDataAccess gd, PatchOp op)
    {
        var bytes = gd.ReadFile(op.Src!) ?? throw new FileNotFoundException($"索引中找不到源文件: {op.Src}");
        var text = DecodeText(bytes, out var enc, out var preamble);
        foreach (var r in op.Replace)
        {
            var occurrences = CountOccurrences(text, r.Old);
            var want = r.Count < 0 ? occurrences : r.Count;
            if (occurrences < want)
                throw new InvalidOperationException(
                    $"源文件 {op.Src} 中 \"{Truncate(r.Old, 40)}\" 只出现 {occurrences} 处（预期 ≥{want}）——客户端不兼容。");
            text = r.Count < 0
                ? text.Replace(r.Old, r.New, StringComparison.Ordinal)
                : ReplaceFirstN(text, r.Old, r.New, r.Count);
        }
        return EncodeText(text, enc, preamble);
    }

    // ═══ datc64 原始字节引擎（指针公式 abs = v + dataOffset - 8，实测验证）═══
    internal sealed record DatLayout(int RowCount, int RowLen, int SepPos, int DataOffset);

    internal sealed record PtrField(int Row, int FieldOffset, PatchState State, string CurrentValue, bool Relative);

    internal static DatLayout ParseLayout(byte[] data)
    {
        var rowCount = BitConverter.ToInt32(data, 0);
        if (rowCount <= 0)
            throw new InvalidOperationException($"datc64 行数非法（{rowCount}）——表结构不认识。");
        var best = 0;
        var sepPos = -1;
        for (var i = 4; i <= data.Length - 8; i++)
        {
            if (data[i] != 0xBB || data[i + 1] != 0xBB || data[i + 2] != 0xBB || data[i + 3] != 0xBB
                || data[i + 4] != 0xBB || data[i + 5] != 0xBB || data[i + 6] != 0xBB || data[i + 7] != 0xBB)
                continue;
            var fixedSize = i - 4;
            if (fixedSize > 0 && fixedSize % rowCount == 0 && fixedSize / rowCount > best)
            {
                best = fixedSize / rowCount;
                sepPos = i;
            }
        }
        if (sepPos < 0)
            throw new InvalidOperationException("无法定位 datc64 分隔符（0xBB×8）——表结构不认识。");
        return new DatLayout(rowCount, best, sepPos, sepPos + 8);
    }

    /// <summary>解码 start 处的 UTF-16LE 字符串。必须按 2 字节步进找 NUL-NUL 终止符——
    /// 按单字节步进会在字符第二个 0x00 与终止符首字节处误判，产生奇数字节截断（U+FFFD）。</summary>
    internal static string DecodeUtf16At(byte[] data, int start)
    {
        var end = start;
        while (end + 1 < data.Length && !(data[end] == 0 && data[end + 1] == 0))
            end += 2;
        return Encoding.Unicode.GetString(data, start, end - start);
    }

    /// <summary>在 blob 中找与 value 精确相等的 UTF-16LE 字符串偏移（无 BOM）。</summary>
    private static int FindExactString(byte[] data, int dataOffset, string value)
    {
        var needle = Encoding.Unicode.GetBytes(value);
        for (var i = dataOffset; i + needle.Length <= data.Length; i++)
        {
            if (!data.AsSpan(i, needle.Length).SequenceEqual(needle))
                continue;
            var start = i;
            while (start >= dataOffset + 2 && !(data[start - 1] == 0 && data[start - 2] == 0))
                start -= 2;
            if (DecodeUtf16At(data, start) == value)
                return start;
        }
        return -1;
    }

    private static (long Value, bool Relative)? PointerAt(byte[] data, int pos, DatLayout layout, string expected)
    {
        var v = BitConverter.ToInt64(data, pos);
        var absRel = (long)v + layout.DataOffset - 8;
        var absAbs = (long)v;
        if (absRel >= layout.DataOffset && absRel < data.Length
            && DecodeUtf16At(data, (int)absRel) == expected)
            return (v, true);
        if (absAbs >= layout.DataOffset && absAbs < data.Length
            && DecodeUtf16At(data, (int)absAbs) == expected)
            return (v, false);
        return null;
    }

    /// <summary>
    /// 按 Id 精确匹配定位行，再找行内解码 == originalPath/newPath 的指针字段。
    /// Id 字符串可能被多行/多位置引用（实测 miscanimated 中 BaseOilGroundBurningEffect 有 2 处引用），
    /// 因此 id 引用与 AOFile 字段是联合约束：逐个引用行验证，取同时满足两者的行。
    /// 若多行都满足联合约束（Id 是跨界解码碎片等），定位不唯一 → 返回 null（拒绝歧义定位）。
    /// </summary>
    internal static PtrField? LocatePtrField(byte[] data, DatLayout layout, string id, string originalPath, string newPath)
    {
        PtrField? found = null;
        for (var q = 4; q + 8 <= layout.SepPos; q++)
        {
            var v = BitConverter.ToInt64(data, q);
            var absRel = (long)v + layout.DataOffset - 8;
            if (absRel < layout.DataOffset || absRel >= data.Length)
                continue;
            if (DecodeUtf16At(data, (int)absRel) != id)
                continue;
            var row = (q - 4) / layout.RowLen;
            if (row >= layout.RowCount)
                continue;

            var rowStart = 4 + row * layout.RowLen;
            for (var g = 0; g + 8 <= layout.RowLen; g++)
            {
                var pos = rowStart + g;
                if (PointerAt(data, pos, layout, originalPath) is { } hitOld)
                {
                    if (found is not null)
                        return null; // 歧义：多行同时满足联合约束，拒绝定位
                    found = new PtrField(row, g, PatchState.NotApplied, originalPath, hitOld.Relative);
                    break;
                }
                if (PointerAt(data, pos, layout, newPath) is { } hitNew)
                {
                    if (found is not null)
                        return null;
                    found = new PtrField(row, g, PatchState.Applied, newPath, hitNew.Relative);
                    break;
                }
            }
            // 该行没有匹配的 AOFile 字段 → 继续找下一个 id 引用行
        }
        return found;
    }

    /// <summary>
    /// 把指针改指 targetString（存在则复用，缺失则追加）。
    /// <paramref name="fixForeignRefs"/> = 单引用约束：apply 时目标路径是补丁专属新路径，任何"非目标行"
    /// 指向它都是异常（典型 = 原指向旧 EOF 的空串哨兵被追加劫持，见 GUIDE 坑 5），改指新空串锚点。
    /// revert 还原到共享的原始路径时必须传 false（原始路径可被多行合法共享）。
    /// </summary>
    private static (byte[] Data, bool Changed) RepointString(byte[] data, DatLayout layout, PtrField field, string targetString, bool fixForeignRefs)
    {
        var targetAbs = FindExactString(data, layout.DataOffset, targetString);
        if (targetAbs < 0)
        {
            targetAbs = data.Length;
            var strBytes = Encoding.Unicode.GetBytes(targetString);
            var append = new byte[strBytes.Length + 2]; // 字符串 + NUL 终止
            strBytes.CopyTo(append, 0);
            var bigger = new byte[data.Length + append.Length];
            Array.Copy(data, bigger, data.Length);
            Array.Copy(append, 0, bigger, data.Length, append.Length);
            data = bigger;
            Log($"[ptr] blob 追加 \"{targetString}\" @0x{targetAbs:X}（+{append.Length} B）");
        }

        var rowStart = 4 + field.Row * layout.RowLen;
        var pos = rowStart + field.FieldOffset;
        var newValue = field.Relative
            ? targetAbs - (layout.DataOffset - 8)
            : targetAbs;
        var oldValue = BitConverter.ToInt64(data, pos);
        var changed = false;
        if (oldValue != newValue)
        {
            BitConverter.GetBytes(newValue).CopyTo(data, pos);
            Log($"[ptr] ROW[{field.Row}] +{field.FieldOffset}: 0x{oldValue:X} -> 0x{newValue:X} (\"{targetString}\")");
            changed = true;
        }

        var foreignFixed = false;
        if (fixForeignRefs)
        {
            var foreign = new List<(int row, int off)>();
            for (var q = 4; q + 8 <= layout.SepPos; q++)
            {
                var row = (q - 4) / layout.RowLen;
                if (row >= layout.RowCount)
                    continue;
                var off = q - 4 - row * layout.RowLen;
                if (row == field.Row && off == field.FieldOffset)
                    continue;
                var v = BitConverter.ToInt64(data, q);
                if (v <= 0)
                    continue;
                var absRel = (long)v + layout.DataOffset - 8;
                var absAbs = (long)v;
                if (absRel == targetAbs || absAbs == targetAbs)
                    foreign.Add((row, off));
            }
            if (foreign.Count > 0)
            {
                // 空串锚点：blob 末尾追加两个 NUL，作为安全空串定位
                var emptyAbs = data.Length;
                var bigger = new byte[data.Length + 2];
                Array.Copy(data, bigger, data.Length);
                data = bigger;
                foreach (var (hRow, hOff) in foreign)
                {
                    var hPos = 4 + hRow * layout.RowLen + hOff;
                    var hOld = BitConverter.ToInt64(data, hPos);
                    var hNew = emptyAbs - (layout.DataOffset - 8);
                    BitConverter.GetBytes(hNew).CopyTo(data, hPos);
                    Log($"[ptr] 哨兵修复 ROW[{hRow}] +{hOff}: 0x{hOld:X} -> 0x{hNew:X}（空串锚点）");
                }
                foreignFixed = true;
            }
        }

        return (data, changed || foreignFixed);
    }

    // ═══ 文本工具 ═══════════════════════════════════════════════
    private static string ReplaceFirstN(string text, string oldText, string newText, int limit)
    {
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        var done = 0;
        while (done < limit)
        {
            var at = text.IndexOf(oldText, pos, StringComparison.Ordinal);
            if (at < 0)
                break;
            sb.Append(text, pos, at - pos).Append(newText);
            pos = at + oldText.Length;
            done++;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private static int CountOccurrences(string text, string needle)
        => needle.Length == 0 ? 0 : text.Split(needle).Length - 1;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    // ═══ 文本编码探测（.ao/.ot 是 UTF-16LE+BOM，但不做假设）═══
    internal static (Encoding Encoding, int Preamble) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    internal static string DecodeText(byte[] bytes, out Encoding encoding, out int preamble)
    {
        (encoding, preamble) = DetectEncoding(bytes);
        return encoding.GetString(bytes, preamble, bytes.Length - preamble);
    }

    private static byte[] EncodeText(string text, Encoding encoding, int preamble)
    {
        var body = encoding.GetBytes(text);
        if (preamble == 0)
            return body;
        var result = new byte[preamble + body.Length];
        var bom = preamble == 3 ? stackalloc byte[] { 0xEF, 0xBB, 0xBF }
            : encoding.CodePage == Encoding.BigEndianUnicode.CodePage
                ? stackalloc byte[] { 0xFE, 0xFF }
                : stackalloc byte[] { 0xFF, 0xFE };
        bom.CopyTo(result);
        body.CopyTo(result, preamble);
        return result;
    }

    private static int CountUnresolved(GameDataAccess gd)
        => gd.Index.Files.Values.Count(f => string.IsNullOrEmpty(f.Path));

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Label(PatchState s) => s switch
    {
        PatchState.NotApplied => "未应用",
        PatchState.Applied => "已应用",
        PatchState.Conflict => "冲突　",
        _ => "不兼容",
    };
}
