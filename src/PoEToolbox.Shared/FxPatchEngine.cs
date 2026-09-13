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

    /// <summary>Index-relative directory every patch bundle lives in.</summary>
    internal const string PatchBundleDirectory = "PATCHED/";

    // ═══ 补丁描述模型 ═══════════════════════════════════════════
    internal sealed class PatchDef
    {
        public string PatchId { get; set; } = "";
        public string BundleName { get; set; } = "";

        /// <summary>补丁内容版本，决定写入哪个 bundle。改了内容就必须升版本，
        /// 否则新内容会被判定为"覆盖其他 Mod"而拒绝执行。</summary>
        public string Version { get; set; } = "";

        public List<PatchOp> Operations { get; set; } = new();

        /// <summary>补丁描述文件所在目录（assets 相对路径的基准）。运行时注入，不序列化。</summary>
        [JsonIgnore]
        public string? BasePath { get; set; }
    }

    internal sealed class PatchOp
    {
        public string Op { get; set; } = "";       // addfile-derived | addfile-asset | patchptr-byid | edittext
        public string? Src { get; set; }           // addfile-derived
        public string? SourceSha256 { get; set; }  // addfile-derived：源文件 SHA-256（可选）
        public string? TargetSha256 { get; set; }  // addfile-derived：生成目标 SHA-256（可选）
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

    // ═══ 补丁 bundle 路径 ═══════════════════════════════════════
    /// <summary>写入目标：<c>PATCHED/&lt;bundleName&gt;_v&lt;version&gt;</c>。
    /// 用版本号而不是执行时间戳，反复 apply/revert 才会复用同一个 bundle，
    /// 而不是每轮都在磁盘上留下一个新文件。</summary>
    internal static string BundlePathOf(PatchDef patch)
        => $"{PatchBundleDirectory}{patch.BundleName}_v{patch.Version}";

    /// <summary>匹配该补丁写过的全部 bundle（任意版本，也兼容旧的时间戳命名）。
    /// 拿它判断某个文件是不是本补丁自己的产物。</summary>
    internal static string BundlePrefixOf(PatchDef patch)
        => $"{PatchBundleDirectory}{patch.BundleName}_";

    /// <summary>该文件当前是否落在本补丁写的 bundle 里——是则可安全覆盖，
    /// 不是则说明被别的 Mod 占用。</summary>
    internal static bool IsPatchOwned(LibBundle3.Index index, PatchDef patch, string path)
        => index.TryGetFile(path, out var fr)
           && fr?.BundleRecord?.Path is { } bundlePath
           && bundlePath.StartsWith(BundlePrefixOf(patch), StringComparison.OrdinalIgnoreCase);

    /// <summary>补丁自己造出来的路径，也就是 purge 唯一允许删除的东西。
    /// 其余落在这个前缀下的文件都来自游戏本体（被重定向进来的还原版表/被改过的资源），
    /// 删掉它们的索引记录等于把文件从游戏里挖走。</summary>
    internal static HashSet<string> OwnedPathsOf(PatchDef patch)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in patch.Operations)
        {
            var isPatchCreated = op.Op switch
            {
                // 副本路径由补丁凭空造出
                "addfile-derived" => true,
                // 没有原版字节 == 原版本来就没有这个文件
                "addfile-asset" => op.OriginalAsset is null,
                // patchptr-byid / edittext 修改的都是游戏原有文件
                _ => false,
            };
            if (isPatchCreated && !string.IsNullOrEmpty(op.Dst))
                owned.Add(op.Dst!);
        }
        return owned;
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
            // fx-patch diff <vanilla> <modified> [-o dir] [--id x] [--bundle y] [--version v]
            if (args.Length > 0 && args[0].Equals("diff", StringComparison.OrdinalIgnoreCase))
                return FxDiff.Run(args[1..]);
            if (args.Length != 3) { Usage(builtInPatch); return 2; }
            gameData = args[0];
            patchPath = args[1];
            action = args[2];
        }

        action = action.ToLowerInvariant();
        if (action is not ("status" or "apply" or "revert" or "cleanup" or "purge")) { Usage(builtInPatch); return 2; }

        PatchDef? patch = null;
        string? patchDir = null;
        string? rawIdHint = null;
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
                rawIdHint = Path.GetFileNameWithoutExtension(path);
                var extracted = ExtractZipPatch(path, out var dir);
                if (extracted is not null)
                    path = extracted;
                else if (dir is not null)
                    patchDir = dir; // 没有 patch.json：交给下面的整包替换识别
                else
                    return 1;
            }
            if (patchDir is null)
            {
                patch = JsonSerializer.Deserialize<PatchDef>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                    ?? throw new InvalidOperationException("补丁描述解析为空。");
                patch.BasePath = Path.GetDirectoryName(Path.GetFullPath(path));
                patchDir = patch.BasePath;
                rawIdHint = patch.PatchId;
            }
        }
        catch (Exception ex)
        {
            LogErr($"补丁描述加载失败: {ex.Message}");
            return 1;
        }

        // 整包替换型补丁：包里带着 _.index.bin 与其他 .bin（bundle），应用 = 备份后整体覆盖同名文件，
        // 不走索引级 op。这类补丁的作者通常直接改客户端文件后打包，没有 patch.json。
        if (!builtInPatch && patchDir is not null && TryDetectRawPack(patchDir, rawIdHint) is { } rawPack)
            return RunRawPack(GameDataLoader.ResolvePath(gameData), rawPack, action);

        if (patch is null)
        {
            LogErr("压缩包内既没有补丁描述（patch.json / *.patch.json），也不是整包替换补丁（需含 _.index.bin 与其他 .bin 文件）。");
            return 1;
        }

        try
        {
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
                "revert" => CmdApplyOrRevert(resolved, patch, apply: false),
                "cleanup" => CmdCleanup(resolved),
                _ => CmdPurge(resolved, patch),
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
    /// 没有 patch.json 时照样解压（返回 null + <paramref name="extractedDir"/>），
    /// 交给整包替换型识别——作者直接打包索引与 bundle 的补丁包就是这样。
    /// 临时目录按天清理，避免残留。
    /// </summary>
    internal static string? ExtractZipPatch(string zipPath) => ExtractZipPatch(zipPath, out _);

    /// <inheritdoc cref="ExtractZipPatch(string)"/>
    /// <param name="extractedDir">解压到的临时目录；没有 patch.json 时（整包替换型补丁包）靠它继续处理。</param>
    internal static string? ExtractZipPatch(string zipPath, out string? extractedDir)
    {
        extractedDir = null;
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var jsonEntry = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Where(e => Path.GetFileName(e.FullName).Equals("patch.json", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(e.FullName).EndsWith(".patch.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Replace('\\', '/').Count(c => c == '/'))
                .FirstOrDefault();

            // 没有 patch.json 就整包解压（不裁剪前缀），让 TryDetectRawPack 去里面找 _.index.bin
            var fullName = jsonEntry?.FullName.Replace('\\', '/');
            var slash = fullName?.LastIndexOf('/') ?? -1;
            var prefix = slash >= 0 ? fullName![..(slash + 1)] : "";

            var baseDir = Path.Combine(Path.GetTempPath(), "poe-toolbox-patch-" + Guid.NewGuid().ToString("N")[..12]);
            CleanStalePatchTempDirs(baseDir);
            extractedDir = baseDir;

            foreach (var entry in archive.Entries)
            {
                var entryName = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = entryName[prefix.Length..];
                if (relative.Length == 0)
                    continue;
                if (!TryGetSafeChildPath(baseDir, relative, out var destination))
                {
                    LogErr($"压缩包包含不安全路径，拒绝解压: {entry.FullName}");
                    try { Directory.Delete(baseDir, recursive: true); } catch { }
                    return null;
                }
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
            if (jsonEntry is null)
            {
                Log("压缩包内没有 patch.json，改按「整包替换型补丁」识别（需要包里有 _.index.bin 与 bundle 文件）。");
                return null;
            }
            return TryGetSafeChildPath(baseDir, fullName![prefix.Length..], out var patchPath)
                ? patchPath
                : null;
        }
        catch (Exception ex)
        {
            LogErr($"解压补丁包失败: {ex.Message}");
            return null;
        }
    }

    internal static bool TryGetSafeChildPath(string rootDirectory, string relativePath, out string destination)
    {
        destination = "";
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            return false;
        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
            return false;

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        destination = candidate;
        return true;
    }

    private static string ResolvePatchFilePath(PatchDef patch, string relativePath)
    {
        var root = patch.BasePath ?? ".";
        if (!TryGetSafeChildPath(root, relativePath, out var path))
            throw new InvalidOperationException($"补丁资源路径不安全: {relativePath}");
        return path;
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
            ? "Usage: fx-oilmod <game-data> <status|apply|revert|cleanup|purge>"
            : "Usage: fx-patch <game-data> <patch.json|patch.zip> <status|apply|revert|cleanup|purge>\n       fx-patch diff <原版index.bin> <修改后index.bin> [-o 目录] [--id x] [--bundle y] [--version v] [--zip]");
    }

    private static int CmdCleanup(string resolved)
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

    private static int CmdPurge(string resolved, PatchDef patch)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再彻底清理补丁文件。");
            return 1;
        }

        var owned = OwnedPathsOf(patch);
        if (owned.Count == 0)
        {
            Log("[完成] 本补丁没有新增文件（只修改了游戏原有文件），无需彻底清理——还原补丁即可。");
            return 0;
        }

        Log($"[清理] {patch.PatchId} v{patch.Version}：只移除本补丁新增的 {owned.Count} 个路径，游戏原有文件一律保留。");
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
        Log($"[完成] 已清理 {removed} 个补丁新增文件；索引中仍被引用的游戏文件保持不动。");
        Log(orphansRemoved == 0
            ? "[收尾] 没有残留的空补丁 Bundle 需要清理。"
            : $"[收尾] 顺带清理了 {orphansRemoved} 个已无引用的补丁 Bundle。");
        return 0;
    }

    private static void ValidatePatch(PatchDef patch)
    {
        if (string.IsNullOrWhiteSpace(patch.PatchId)) throw new InvalidOperationException("缺少 patchId。");
        if (string.IsNullOrWhiteSpace(patch.BundleName)) throw new InvalidOperationException("缺少 bundleName。");
        // version 会被拼进 bundle 路径，必须限制字符集
        if (string.IsNullOrWhiteSpace(patch.Version)) throw new InvalidOperationException("缺少 version。");
        if (patch.Version.Contains("..")) throw new InvalidOperationException("version 不能包含 '..'。");
        foreach (var c in patch.Version)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                throw new InvalidOperationException($"version 只能包含字母、数字、点、下划线和短横线：\"{patch.Version}\"。");
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
            if (op.SourceSha256 is not null && !IsSha256(op.SourceSha256))
                throw new InvalidOperationException("addfile-derived 的 sourceSha256 必须是 64 位十六进制 SHA-256。");
            if (op.TargetSha256 is not null && !IsSha256(op.TargetSha256))
                throw new InvalidOperationException("addfile-derived 的 targetSha256 必须是 64 位十六进制 SHA-256。");
        }
    }

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

    internal static int RunRawPack(string resolved, RawPack pack, string action)
    {
        if (!resolved.EndsWith(".index.bin", StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
        {
            LogErr("[中止] 整包替换补丁只支持把索引放在独立文件里的客户端（Bundles2/_.index.bin）。");
            return 1;
        }

        Log($"[整包替换] {pack.PatchId}：{pack.Files.Count} 个文件（含索引本体）");
        return action switch
        {
            "status" => RawStatus(resolved, pack),
            "apply" => RawApply(resolved, pack),
            "revert" => RawRevert(resolved, pack),
            _ => RawNoBundleOperation(action),
        };
    }

    private static int RawNoBundleOperation(string action)
    {
        Log($"[提示] 整包替换补丁不写 PATCHED bundle，{action} 对它不适用；要撤销请点「还原补丁」。");
        return 0;
    }

    /// <summary>应用：先把游戏目录里的同名文件备份到 <c>backup/&lt;补丁名&gt;/</c>，再逐个覆盖。</summary>
    private static int RawApply(string resolved, RawPack pack)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再应用补丁。");
            return 1;
        }

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
                    Log($"[备份] {file.RelativePath} 与原版基线一致，复用 {Path.GetFileName(baselinePath)}");
                }
                else if (File.Exists(backupFile))
                {
                    backupSource = file.RelativePath;
                    Log($"[备份] {file.RelativePath} 之前已备份，保留最早的原始版本");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(target, backupFile);
                    backupSource = file.RelativePath;
                    Log($"[备份] {file.RelativePath} → backup/{pack.PatchId}/{file.RelativePath}");
                }
                kind = "replaced";
            }
            else
            {
                kind = "added";
                Log($"[备份] 游戏目录原本没有 {file.RelativePath}（补丁新增），无需备份");
            }

            File.Copy(file.SourcePath, target, overwrite: true);
            Log($"[覆盖] {file.RelativePath}（{new FileInfo(file.SourcePath).Length:N0} B）");
            manifest.Add($"{kind}\t{file.RelativePath}\t{backupSource}");
        }

        File.WriteAllLines(Path.Combine(backupDir, RawManifestFileName), manifest);
        Log($"[成功] {pack.PatchId} 应用完成：{pack.Files.Count} 个文件已写入 {indexDir}");
        Log($"       原始文件备份在 {backupDir}，点「还原补丁」即可放回去。");
        return 0;
    }

    /// <summary>还原：按清单把备份的文件放回原位；补丁新增的文件（原版没有）则删除。</summary>
    private static int RawRevert(string resolved, RawPack pack)
    {
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再还原补丁。");
            return 1;
        }

        var indexDir = Path.GetDirectoryName(resolved)!;
        var backupDir = Path.Combine(IndexBackupService.GetBackupDirectory(resolved), pack.PatchId);
        var baselinePath = IndexBackupService.GetBaselinePath(resolved);
        var manifest = ReadRawManifest(Path.Combine(backupDir, RawManifestFileName));

        foreach (var file in pack.Files)
        {
            var target = Path.Combine(indexDir, file.RelativePath);
            manifest.TryGetValue(file.RelativePath, out var entry);

            if (entry?.Kind == "added")
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    Log($"[删除] {file.RelativePath}（补丁新增，原版没有）");
                }
                else
                    Log($"[跳过] {file.RelativePath} 已不存在");
                continue;
            }

            var source = entry?.BackupSource == "baseline"
                ? baselinePath
                : Path.Combine(backupDir, file.RelativePath);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                Log($"[还原] {file.RelativePath} ← {source}");
            }
            else
                LogErr($"[警告] {file.RelativePath} 找不到原始备份，未改动（可从 {backupDir} 手动恢复，或用「还原原版索引」）");
        }

        Log($"[成功] {pack.PatchId} 还原完成。");
        return 0;
    }

    private static int RawStatus(string resolved, RawPack pack)
    {
        var indexDir = Path.GetDirectoryName(resolved)!;
        var backupDir = Path.Combine(IndexBackupService.GetBackupDirectory(resolved), pack.PatchId);
        Log($"索引目录: {indexDir}");
        Log($"补丁: {pack.PatchId}（整包替换型，{pack.Files.Count} 个文件）");
        Log($"备份目录: {backupDir}{(Directory.Exists(backupDir) ? "" : "（尚不存在，应用时自动创建）")}");

        var applied = 0;
        foreach (var file in pack.Files)
        {
            var target = Path.Combine(indexDir, file.RelativePath);
            var state = File.Exists(target) && SameContent(target, file.SourcePath) ? "已应用" : "未应用";
            if (state == "已应用")
                applied++;
            var backedUp = File.Exists(Path.Combine(backupDir, file.RelativePath)) ? "已备份" : "无备份";
            Log($"  [{state}] {file.RelativePath}（{backedUp}）");
        }

        Log($"[结果] {applied}/{pack.Files.Count} 个文件已是补丁内容。");
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

    // ═══ status ═════════════════════════════════════════════════
    private static int CmdStatus(string resolved, PatchDef patch)
    {
        try
        {
            using var gd = GameDataAccess.OpenReadOnlyMapped(resolved);
            Log($"索引: {gd.GameDataPath}");
            Log($"补丁: {patch.PatchId}");
            foreach (var s in ComputeStates(gd, patch))
                Log($"  [{Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");
            return 0;
        }
        finally
        {
            // Opened outside GameDataLoader, so the reclaim has to be requested here as well:
            // "查看状态" is a button in the plugin and repeats, so the index must not pile up.
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }
    }

    // ═══ apply / revert ═════════════════════════════════════════
    private static int CmdApplyOrRevert(string resolved, PatchDef patch, bool apply)
    {
        var verb = apply ? "应用" : "还原";

        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] Path of Exile 正在运行。请先退出游戏，再执行补丁应用或还原；背包清理使用独立的运行态流程。");
            return 1;
        }

        // Pass 1：只读预检，冲突/不兼容在写入前中止
        int unresolvedBefore;
        OpState[] preview;
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            unresolvedBefore = CountUnresolved(gd);
            Log($"[预检] {patch.PatchId} → {(apply ? "apply" : "revert")}");
            preview = ComputeStates(gd, patch, apply);
            foreach (var s in preview)
                Log($"  [{Label(s.State)}] {s.Op.Describe}\n          └ {s.Detail}");
            if (preview.Any(s => s.State is PatchState.Conflict or PatchState.Incompatible))
            {
                LogErr("[中止] 存在冲突或不兼容操作，未做任何写入。");
                return 1;
            }
        }

        // 预检期间游戏可能被重新启动，写入前再检查一次。
        if (PoeDetector.Default.IsPoeRunning())
        {
            LogErr("[中止] 检测到 Path of Exile 已启动，未做任何写入。");
            return 1;
        }

        // Pass 2：写入（独立 bundle：PATCHED/<bundleName>_v<version>.bundle.bin，后缀由库追加）
        var bundlePath = BundlePathOf(patch);
        var exit = GameDataLoader.Use(resolved, GameDataMode.ReadWrite, gd =>
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
                    var opChanged = ExecuteOp(gd, patch, op, apply);
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
        if (exit != 0)
            return exit;

        // Pass 3：重开校验
        try
        {
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
        }
        finally
        {
            // Pass 3 is a second full open of the index in the same process. Apply and revert are
            // usually run back to back, so the verification copy must not sit on the heap either.
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }

        Log($"[成功] {patch.PatchId} {verb}完成。");
        return 0;
    }

    // ═══ 单 op 三态计算（只读）══════════════════════════════════
    private static OpState[] ComputeStates(GameDataAccess gd, PatchDef patch, bool? applying = null)
        => patch.Operations.Select(op => ComputeState(gd, patch, op, applying)).ToArray();

    private static OpState ComputeState(GameDataAccess gd, PatchDef patch, PatchOp op, bool? applying = null)
    {
        try
        {
            switch (op.Op)
            {
                case "addfile-derived":
                {
                    if (!gd.FileExists(op.Src!))
                        return new OpState(op, PatchState.Incompatible, $"找不到源文件 {op.Src}");
                    if (applying == false)
                        return gd.FileExists(op.Dst!)
                            ? new OpState(op, PatchState.Applied, $"副本存在 {op.Dst}（还原时保留无引用副本）")
                            : new OpState(op, PatchState.NotApplied, "副本不存在");
                    var source = gd.ReadFile(op.Src!)!;
                    var sourceSha = Sha(source);
                    if (op.SourceSha256 is not null
                        && !sourceSha.Equals(op.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        return new OpState(op, PatchState.Incompatible,
                            $"源文件 SHA-256 不匹配：实际 {sourceSha}，预期 {op.SourceSha256}");

                    var target = BuildDerivedContent(source, op, out var targetSha);
                    if (op.TargetSha256 is not null
                        && !targetSha.Equals(op.TargetSha256, StringComparison.OrdinalIgnoreCase))
                        return new OpState(op, PatchState.Incompatible,
                            $"目标内容 SHA-256 不匹配：生成 {targetSha}，预期 {op.TargetSha256}");
                    if (!gd.FileExists(op.Dst!))
                        return new OpState(op, PatchState.NotApplied, "副本不存在");

                    var existingSha = Sha(gd.ReadFile(op.Dst!)!);
                    return existingSha.Equals(targetSha, StringComparison.OrdinalIgnoreCase)
                        ? new OpState(op, PatchState.Applied, $"副本内容 SHA-256 匹配 {targetSha}")
                        : new OpState(op, PatchState.Conflict,
                            $"目标文件已存在但内容指纹不匹配：实际 {existingSha}，预期 {targetSha}（可能被其他 Mod 占用）");
                }
                case "addfile-asset":
                {
                    var assetPath = ResolvePatchFilePath(patch, op.Asset!);
                    if (!File.Exists(assetPath))
                        return new OpState(op, PatchState.Incompatible, $"补丁包内找不到 asset {op.Asset}");
                    if (!gd.FileExists(op.Dst!))
                        return new OpState(op, PatchState.NotApplied, "目标文件不存在");
                    var sha = Sha(gd.ReadFile(op.Dst!)!);
                    if (sha == Sha(File.ReadAllBytes(assetPath)))
                        return new OpState(op, PatchState.Applied, "内容与补丁 asset 一致");
                    if (op.OriginalAsset is not null)
                    {
                        var origPath = ResolvePatchFilePath(patch, op.OriginalAsset);
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
                var assetPath = ResolvePatchFilePath(patch, op.Asset!);
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
                    gd.AddFile(op.Dst!, newBytes, saveIndex: false);
                    Log($"[op] 新增 {op.Dst}（{newBytes.Length:N0} B，asset）");
                    return true;
                }
                // revert：有原版字节才还原；新增类（无 OriginalAsset）保留
                if (op.OriginalAsset is null)
                {
                    Log($"[skip] {op.Dst} 保留（新增内容，无原版字节可还原）");
                    return false;
                }
                var origPath = ResolvePatchFilePath(patch, op.OriginalAsset);
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
                var source = gd.ReadFile(op.Src!) ?? throw new FileNotFoundException($"索引中找不到源文件: {op.Src}");
                var sourceSha = Sha(source);
                if (op.SourceSha256 is not null && !sourceSha.Equals(op.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"源文件 SHA-256 不匹配：实际 {sourceSha}，预期 {op.SourceSha256}");
                var content = BuildDerivedContent(source, op, out var targetSha);
                if (op.TargetSha256 is not null && !targetSha.Equals(op.TargetSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"目标内容 SHA-256 不匹配：生成 {targetSha}，预期 {op.TargetSha256}");
                if (gd.FileExists(op.Dst!))
                {
                    var existingSha = Sha(gd.ReadFile(op.Dst!)!);
                    if (existingSha.Equals(targetSha, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"[skip] {op.Dst} 已存在且内容指纹正确");
                        return false;
                    }
                    // 本补丁上一版留下的副本要放行（否则升版本时补丁会被自己挡住）；
                    // 真正被其他 Mod 占用的仍然拒绝。
                    if (!IsPatchOwned(gd.Index, patch, op.Dst!))
                        throw new InvalidOperationException($"目标文件 {op.Dst} 已存在但内容指纹不匹配（实际 {existingSha}，预期 {targetSha}），拒绝覆盖其他 Mod。");
                    gd.Index.TryGetFile(op.Dst!, out var ownCopy);
                    ownCopy!.Write(content);
                    Log($"[op] 覆盖本补丁旧版副本 {op.Dst}（{content.Length:N0} B）");
                    return true;
                }
                gd.AddFile(op.Dst!, content, saveIndex: false);
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
                var (newDat, changed) = RepointString(dat, layout, field, target);
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
    private static byte[] BuildDerivedContent(byte[] bytes, PatchOp op, out string targetSha)
    {
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
        var result = EncodeText(text, enc, preamble);
        targetSha = Sha(result);
        return result;
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
    /// 只修改定位到的字段，不重写其他行的 foreign reference。
    /// 这样 apply/revert 都是严格成对的单字段变换，不会把其他行改成不可逆的哨兵引用。
    /// </summary>
    private static (byte[] Data, bool Changed) RepointString(byte[] data, DatLayout layout, PtrField field, string targetString)
    {
        var targetAbs = FindExactString(data, layout.DataOffset, targetString);
        if (targetAbs < 0)
        {
            // Preserve the old EOF as an empty-string sentinel. Some foreign rows may
            // legitimately point at that offset; appending the target directly would
            // silently retarget those rows and make apply/revert non-reversible.
            targetAbs = data.Length + 2;
            var strBytes = Encoding.Unicode.GetBytes(targetString);
            var append = new byte[strBytes.Length + 4]; // empty sentinel + string + NUL
            strBytes.CopyTo(append, 2);
            var bigger = new byte[data.Length + append.Length];
            Array.Copy(data, bigger, data.Length);
            Array.Copy(append, 0, bigger, data.Length, append.Length);
            data = bigger;
            Log($"[ptr] blob 追加空串哨兵 + \"{targetString}\" @0x{targetAbs:X}（+{append.Length} B）");
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

        return (data, changed);
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

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Label(PatchState s) => s switch
    {
        PatchState.NotApplied => "未应用",
        PatchState.Applied => "已应用",
        PatchState.Conflict => "冲突　",
        _ => "不兼容",
    };
}
