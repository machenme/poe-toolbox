using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PoEToolbox.Shared;
using System.IO;

/// <summary>
/// fx-patch diff：对比"游戏原版 index.bin"与"作者修改后 index.bin"，自动生成补丁描述文件。
/// 三层漏斗避免全量内容 hash：PathHash 字典匹配 → record 三元组（bundle 路径+offset+size）→ 仅候选文件读内容。
/// 自动归类：dat 表默认整表 asset（--dat-ptr opt-in 逐行 patchptr）；文本 → edittext；二进制/新增 → addfile-asset。
/// 已知边界：整包 bundle 替换式修改（record 三元组不变、仅 bundle 内容被换）在 record 层不可见，无法被 diff 捕捉。
/// </summary>
public static class FxDiff
{
    private static readonly AsyncLocal<Action<string>?> LogSinkCurrent = new();

    /// <summary>UI 日志钩子：设置后所有输出同时流入该回调。按异步流隔离，并发执行互不覆盖。</summary>
    public static Action<string>? LogSink
    {
        get => LogSinkCurrent.Value;
        set => LogSinkCurrent.Value = value;
    }

    private static void Log(string msg)
    {
        LogSink?.Invoke(msg);
        Console.WriteLine(msg);
        FileLogger.App.Info($"[fx-patch diff] {msg}");
    }

    private static void LogErr(string msg)
    {
        LogSink?.Invoke("[错误] " + msg);
        Console.Error.WriteLine(msg);
        FileLogger.App.Error($"[fx-patch diff] {msg}");
    }

    public static int Run(string[] args)
    {
        // diff <原版index> <修改后index> [-o 输出目录] [--id patchId] [--bundle bundleName] [--version 版本] [--zip]
        string? vanilla = null, modified = null, outDir = null, patchId = null, bundleName = null, version = null;
        var datPtr = false;
        var zip = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-o" or "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (a == "--id" && i + 1 < args.Length) patchId = args[++i];
            else if (a == "--bundle" && i + 1 < args.Length) bundleName = args[++i];
            else if (a == "--version" && i + 1 < args.Length) version = args[++i];
            else if (a == "--dat-ptr") datPtr = true;
            else if (a == "--zip") zip = true;
            else if (vanilla is null) vanilla = a;
            else if (modified is null) modified = a;
            else { LogErr($"多余参数: {a}"); return 2; }
        }
        if (vanilla is null || modified is null)
        {
            LogErr("Usage: fx-patch diff <原版index.bin> <修改后index.bin> [-o 输出目录] [--id patchId] [--bundle bundleName] [--version 版本] [--zip]");
            return 2;
        }
        patchId ??= DefaultPatchId();
        // bundle 名不能用一个固定值：多个补丁共用前缀时，相同版本号会互相覆盖
        bundleName ??= SanitizeBundleName(patchId);

        try
        {
            // 两份索引是同一游戏安装的快照，bundle 一律按"修改后索引"所在目录解析
            //（原版快照可能躺在备份目录里，其 bundle 仍在游戏 Bundles2 下）
            var resolvedVanilla = GameDataAccess.ResolvePath(vanilla);
            var resolvedModified = GameDataAccess.ResolvePath(modified);
            var bundleDir = Path.GetDirectoryName(resolvedModified)!;
            // 默认输出到 toolbox 数据目录 %LOCALAPPDATA%\PoEToolbox\patches\<patchId>\
            outDir ??= Path.Combine(ConfigService.PatchesDirectory, patchId);
            using var gdA = GameDataAccess.OpenReadOnlyMapped(resolvedVanilla, bundleDir);
            using var gdB = GameDataAccess.OpenReadOnlyMapped(resolvedModified, bundleDir);
            var code = Diff(gdA, gdB, outDir, patchId, bundleName, version, datPtr);
            if (code == 0 && zip)
            {
                var zipPath = outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
                try
                {
                    CreateZip(outDir, zipPath);
                    Log($"[完成] 补丁包已压缩: {zipPath}");
                }
                catch (Exception ex)
                {
                    LogErr($"压缩补丁包失败: {ex.Message}");
                    return 1;
                }
            }
            return code;
        }
        catch (Exception ex)
        {
            LogErr($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            // Both indexes used to be held until the next natural collection: this is the only
            // place in the toolbox that has two of them alive at once (~2 GB), so hand the memory
            // back explicitly once the diff is written.
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }
    }

    private static int Diff(GameDataAccess gdA, GameDataAccess gdB, string outDir, string patchId, string bundleName, string? version, bool datPtr)
    {
        Log($"原版  : {gdA.GameDataPath}");
        Log($"修改后: {gdB.GameDataPath}");

        // ── 第一/二层：PathHash 匹配 + record 三元组对比（零内容读取）──
        var added = new List<(string Path, byte[] Content)>();
        var changed = new List<(string Path, byte[] Old, byte[] New)>();
        var removed = new List<string>();
        long common = 0;

        foreach (var fr in gdB.Index.Files.Values)
        {
            var path = fr.Path;
            if (path is null || IsToolArtifact(path))
                continue;
            if (!gdA.Index.Files.TryGetValue(fr.PathHash, out var va) || va is null)
            {
                added.Add((path, fr.Read().ToArray()));
                continue;
            }
            common++;
            if (va.BundleRecord.Path == fr.BundleRecord.Path && va.Offset == fr.Offset && va.Size == fr.Size)
                continue; // record 一致 → 内容一致（同客户端 bundle 不可变）
            var oldB = va.Read().ToArray();
            var newB = fr.Read().ToArray();
            if (FxPatchEngine.Sha(oldB) == FxPatchEngine.Sha(newB))
                continue; // record 不同但内容相同（例如重定位），跳过
            changed.Add((path, oldB, newB));
        }
        foreach (var fr in gdA.Index.Files.Values)
        {
            if (fr.Path is null || IsToolArtifact(fr.Path))
                continue;
            if (!gdB.Index.Files.ContainsKey(fr.PathHash))
                removed.Add(fr.Path);
        }

        Log($"共同文件: {common:N0} | 新增: {added.Count} | 变更: {changed.Count} | 消失: {removed.Count}");
        foreach (var p in removed.Take(20))
            Log($"  [警告] 原版有而修改后没有（补丁引擎无法删除文件，忽略）: {p}");

        // ── 第三层：候选文件归类 ──
        var ops = new List<FxPatchEngine.PatchOp>();
        var assetIndex = 0;
        Directory.CreateDirectory(Path.Combine(outDir, "assets"));

        foreach (var (path, content) in added)
        {
            var asset = WriteAsset(outDir, assetIndex++, path, content, null, out _);
            ops.Add(new FxPatchEngine.PatchOp { Op = "addfile-asset", Dst = path, Asset = asset });
            Log($"  [新增→asset] {path}（{content.Length:N0} B）");
        }

        foreach (var (path, oldB, newB) in changed)
        {
            // 1) dat 表：默认整表 asset（100% 可靠）；--dat-ptr 启用逐行 diff → patchptr-byid
            //    （字符串密集表的碎片锚点难以完全排除，故逐行模式需显式 opt-in）
            if (path.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dat64", StringComparison.OrdinalIgnoreCase))
            {
                if (datPtr)
                {
                    var datOps = TryDatDiff(path, oldB, newB);
                    if (datOps is not null)
                    {
                        Log($"  [dat→{datOps.Count} 个指针 op] {path}");
                        ops.AddRange(datOps);
                        continue;
                    }
                    Log($"  [dat] {path} 行布局有差异或无法安全表达 → 整表 asset");
                }
                var a2 = WriteAsset(outDir, assetIndex++, path, newB, oldB, out var orig2);
                ops.Add(new FxPatchEngine.PatchOp { Op = "addfile-asset", Dst = path, Asset = a2, OriginalAsset = orig2 });
                Log($"  [dat→asset] {path}（{newB.Length:N0} B）");
                continue;
            }

            // 2) 文本文件 → 公共前后缀 → edittext
            if (IsTextFile(path) && TryTextDiff(oldB, newB, out var midOld, out var midNew))
            {
                ops.Add(new FxPatchEngine.PatchOp { Op = "edittext", Path = path, Old = midOld, New = midNew });
                Log($"  [文本→edittext] {path}（改动 {midOld.Length}→{midNew.Length} 字符）");
                continue;
            }

            // 3) 兜底：成品字节 asset
            var asset = WriteAsset(outDir, assetIndex++, path, newB, oldB, out var orig);
            ops.Add(new FxPatchEngine.PatchOp { Op = "addfile-asset", Dst = path, Asset = asset, OriginalAsset = orig });
            Log($"  [二进制→asset] {path}（{newB.Length:N0} B{(orig is null ? "，原版过大未存 revert 字节" : "")}）");
        }

        if (ops.Count == 0)
        {
            Log("[结果] 两份索引在文件层无差异——没有可生成的补丁。");
            return 1;
        }

        // ── 写出补丁描述 ──
        var patch = new FxPatchEngine.PatchDef
        {
            PatchId = patchId,
            BundleName = bundleName,
            Version = version,
            Operations = ops,
        };
        var json = JsonSerializer.Serialize(patch,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
        var patchPath = Path.Combine(outDir, $"{patchId}.patch.json");
        File.WriteAllText(patchPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log($"[完成] 补丁已生成: {patchPath}");
        Log($"       共 {ops.Count} 个操作。应用: fx-patch <game-data> \"{patchPath}\" apply");
        return 0;
    }

    /// <summary>dat 表逐行 diff：同布局前提下，行为单位对比；锚点（两版一致的字符串字段）作 Id，
    /// 变化的指针字段生成 patchptr-byid。任何无法表达的差异 → null（调用方 fallback asset）。</summary>
    private static List<FxPatchEngine.PatchOp>? TryDatDiff(string path, byte[] oldB, byte[] newB)
    {
        FxPatchEngine.DatLayout la, lb;
        try
        {
            la = FxPatchEngine.ParseLayout(oldB);
            lb = FxPatchEngine.ParseLayout(newB);
        }
        catch (Exception ex)
        {
            // 布局解析失败按"无法逐行 diff"回退整表 asset，但留下日志便于区分引擎缺陷与真实布局差异
            FileLogger.App.Debug($"TryDatDiff 布局解析失败（{path}），回退整表 asset: {ex.Message}");
            return null;
        }
        if (la.RowCount != lb.RowCount || la.RowLen != lb.RowLen || la.DataOffset != lb.DataOffset)
            return null;

        var ops = new List<FxPatchEngine.PatchOp>();
        var skippedRows = new List<string>();
        for (var r = 0; r < la.RowCount; r++)
        {
            var start = 4 + r * la.RowLen;
            if (oldB.AsSpan(start, la.RowLen).SequenceEqual(newB.AsSpan(start, lb.RowLen)))
                continue;
            var fa = DecodeRowFields(oldB, la, r);
            var fb = DecodeRowFields(newB, lb, r);
            string? anchor = null;
            var rowUsable = fa.Count == fb.Count;
            if (rowUsable)
            {
                foreach (var (off, s) in fa)
                {
                    if (fb.TryGetValue(off, out var s2) && s2 == s)
                    {
                        anchor = s;
                        break;
                    }
                }
                rowUsable = anchor is not null;
            }
            if (!rowUsable)
            {
                skippedRows.Add($"ROW[{r}]（找不到可配对的锚点字段）");
                continue;
            }
            var rowOps = 0;
            foreach (var (off, sA) in fa)
            {
                if (!fb.TryGetValue(off, out var sB) || sB == sA)
                    continue;
                // 变更字段必须是"像路径的字符串"——过滤非对齐跨界解码的假阳性
                //（真路径形如 Metadata/...、Art/...；垃圾解码常是路径尾巴或无分隔符碎片）
                if (!LooksLikePath(sA) || !LooksLikePath(sB))
                {
                    skippedRows.Add($"ROW[{r}]（存在非路径样变更: \"{FxTruncate(sA, 30)}\" → \"{FxTruncate(sB, 30)}\"）");
                    rowOps = -1;
                    break;
                }
                // 生成前唯一性验证：该 op 必须能在原版表上被 LocatePtrField 唯一定位，
                // 且命中的行/字段与本 diff 所见一致。碎片锚点（跨界解码）在此被拒绝。
                var probe = FxPatchEngine.LocatePtrField(oldB, la, anchor!, sA, sB);
                if (probe is null || probe.Row != r || probe.FieldOffset != off
                    || probe.State != FxPatchEngine.PatchState.NotApplied)
                {
                    skippedRows.Add($"ROW[{r}]（定位验证失败: \"{FxTruncate(sA, 30)}\"，锚点可能不唯一）");
                    rowOps = -1;
                    break;
                }
                ops.Add(new FxPatchEngine.PatchOp
                {
                    Op = "patchptr-byid",
                    Table = path,
                    Id = anchor,
                    OriginalPath = sA,
                    NewPath = sB,
                });
                rowOps++;
            }
            if (rowOps < 0)
                continue;
            if (rowOps == 0)
                skippedRows.Add($"ROW[{r}]（字节有差异但无可表达的指针变更）");
        }
        foreach (var w in skippedRows.Take(10))
            Log($"  [警告] {path} {w} —— 该行变更不会进入补丁！");
        if (skippedRows.Count > 10)
            Log($"  [警告] {path} 共 {skippedRows.Count} 行被跳过。");
        if (ops.Count > 200)
            return null; // 单表指针变更过多，可疑 → 整表 asset 兜底
        return ops;
    }

    private static string FxTruncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static bool LooksLikePath(string s)
        => s.Length >= 5 && (s.Contains('/') || s.Contains('.')) && !s.StartsWith('/');

    /// <summary>解码一行内所有"指针可解析且内容为可打印 ASCII"的字符串字段，按字段偏移索引。
    /// 仅接受 4 字节对齐的字段位置——未对齐位置几乎都是跨界解码碎片（假锚点/假路径的来源）。</summary>
    private static Dictionary<int, string> DecodeRowFields(byte[] data, FxPatchEngine.DatLayout layout, int row)
    {
        var result = new Dictionary<int, string>();
        var rowStart = 4 + row * layout.RowLen;
        for (var g = 0; g + 8 <= layout.RowLen; g += 4)
        {
            var v = BitConverter.ToInt64(data, rowStart + g);
            if (v <= 0)
                continue;
            var abs = (long)v + layout.DataOffset - 8;
            if (abs < layout.DataOffset || abs >= data.Length)
                continue;
            var s = FxPatchEngine.DecodeUtf16At(data, (int)abs);
            if (s.Length is < 3 or > 260)
                continue;
            var printable = true;
            foreach (var c in s)
            {
                if (c < 0x20 || c > 0x7E)
                {
                    printable = false;
                    break;
                }
            }
            if (printable)
                result[g] = s;
        }
        return result;
    }

    /// <summary>文本 diff：找公共前后缀，中间段作为 edittext 的 old/new。中间段过大或为纯插入时失败。</summary>
    private static bool TryTextDiff(byte[] oldB, byte[] newB, out string midOld, out string midNew)
    {
        midOld = midNew = "";
        var a = FxPatchEngine.DecodeText(oldB, out _, out _);
        var b = FxPatchEngine.DecodeText(newB, out _, out _);
        var p = 0;
        while (p < a.Length && p < b.Length && a[p] == b[p])
            p++;
        var s = 0;
        while (s < a.Length - p && s < b.Length - p && a[a.Length - 1 - s] == b[b.Length - 1 - s])
            s++;
        midOld = a[p..(a.Length - s)];
        midNew = b[p..(b.Length - s)];
        // 纯插入（old 为空串）无法表达为替换；中间段过大也放弃
        return midOld.Length > 0 && midOld.Length <= 64 * 1024 && midNew.Length <= 64 * 1024;
    }

    private static bool IsTextFile(string path)
        => path.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ot", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".aoc", StringComparison.OrdinalIgnoreCase);

    /// <summary>把成品字节写进 assets/；原版字节一并保存供 revert 与"原版识别"（不设上限——
    /// 没有原版字节的 asset 会在干净原版索引上被误判 Conflict）。</summary>
    private static string WriteAsset(string outDir, int index, string srcPath, byte[] newContent, byte[]? oldContent, out string? originalAsset)
    {
        var ext = System.IO.Path.GetExtension(srcPath);
        var name = $"assets/{index:D4}_{Sanitize(srcPath)}{ext}";
        File.WriteAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar)), newContent);
        originalAsset = null;
        if (oldContent is not null)
        {
            originalAsset = $"assets/{index:D4}_{Sanitize(srcPath)}.orig{ext}";
            File.WriteAllBytes(Path.Combine(outDir, originalAsset.Replace('/', Path.DirectorySeparatorChar)), oldContent);
        }
        return name;
    }

    private static string Sanitize(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var c in System.IO.Path.GetFileNameWithoutExtension(path))
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    /// <summary>默认补丁 ID：留空名字时使用的时间戳名（diff-年月日-时分）。</summary>
    public static string DefaultPatchId() => $"diff-{DateTime.Now:yyyyMMdd-HHmm}";

    /// <summary>由用户的自定义名字得到补丁 ID；名字为空/全是非法字符时回落到默认时间戳名。
    /// 补丁 ID 同时是输出目录名、json 文件名与 bundle 名来源，因此必须去掉文件名非法字符。</summary>
    public static string MakePatchId(string? customName)
    {
        var name = customName?.Trim() ?? "";
        if (name.Length == 0)
            return DefaultPatchId();

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        var id = sb.ToString().Trim('_', '-', '.');
        return id.Length == 0 ? DefaultPatchId() : id;
    }

    /// <summary>把补丁目录整体打包成 zip：zip 根目录就是 assets/ 与补丁描述 json 两项，
    /// 与 <c>FxPatchEngine.ExtractZipPatch</c> 的"压缩目录内容"形态一致（解压后可直接 apply）。</summary>
    internal static void CreateZip(string outDir, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(outDir, file).Replace('\\', '/');
            var entry = zip.CreateEntry(relative, System.IO.Compression.CompressionLevel.Optimal);
            using var source = File.OpenRead(file);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }

    /// <summary>把补丁 ID 降成可做 bundle 路径片段的名字。
    /// 只保留 ASCII 字母数字与 <c>-</c>/<c>_</c>，点号也替换掉，避免出现会被路径校验拒绝的 <c>..</c>。</summary>
    private static string SanitizeBundleName(string patchId)
    {
        var sb = new StringBuilder(patchId.Length);
        foreach (var c in patchId)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        var name = sb.ToString().Trim('_', '-');
        return name.Length == 0 ? "Patch" : name;
    }

    private static bool IsToolArtifact(string path)
        => path.StartsWith("LibGGPK3/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("PATCHED/", StringComparison.OrdinalIgnoreCase);
}
