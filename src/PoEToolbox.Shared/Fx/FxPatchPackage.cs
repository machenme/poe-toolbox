using System.IO;
using System.Text.Json;

namespace PoEToolbox.Shared;

/// <summary>
/// 补丁包的入口侧：解析 <c>.patch.json</c>、安全解压 zip、校验声明、定位包内资源路径。
/// 只碰文件与 JSON，不碰索引——落盘的分支留在 <see cref="FxPatchEngine"/>。
/// </summary>
/// <remarks>
/// S4 从 <see cref="FxPatchEngine"/> 原样搬出，方法体未改。日志仍走引擎那一个出口
/// （<c>FxPatchEngine.Log</c> / <c>LogErr</c>），否则 UI 的 LogSink 会漏掉解压输出。
/// 可见性与基线一致：只有引擎会调的（解析、解压、资源路径）是 internal，包内自用的仍是 private。
/// </remarks>
internal static class FxPatchPackage
{
    private static readonly JsonSerializerOptions PatchJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>解析并校验补丁描述；<paramref name="basePath"/> 是包内资源（assets）相对路径基准，可为 null。</summary>
    internal static PatchDef ParsePatchJson(string json, string? basePath)
    {
        var patch = JsonSerializer.Deserialize<PatchDef>(json, PatchJsonOptions)
            ?? throw new InvalidOperationException("补丁描述解析为空。");
        patch.BasePath = basePath;
        ValidatePatch(patch);
        return patch;
    }

    /// <summary>读取并校验一份 .patch.json，注入 BasePath（assets 相对路径基准）。</summary>
    internal static PatchDef LoadPatchFile(string path)
        => ParsePatchJson(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    /// <summary>
    /// 把 zip 补丁包解压到临时目录并返回其中 patch.json 的路径。
    /// 兼容两种打包方式：压缩整个目录（顶层有一层文件夹）或压缩目录内容（patch.json 在 zip 根）。
    /// 没有 patch.json 时照样解压（返回 null + <paramref name="extractedDir"/>），
    /// 交给整包替换型识别——作者直接打包索引与 bundle 的补丁包就是这样。
    /// 解压目录放在工具箱数据目录下，超过 14 天的旧目录在下次解压时清理。
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

            // 解压到工具箱数据目录而不是 %TEMP%：补丁应用后 status/revert 仍需读取包内资源，
            // %TEMP% 会被系统随时回收，导致已解压的补丁包静默失效。
            var extractRoot = Path.Combine(ConfigService.PatchesDirectory, "_extracted");
            Directory.CreateDirectory(extractRoot);
            var baseDir = Path.Combine(extractRoot, "poe-toolbox-patch-" + Guid.NewGuid().ToString("N")[..12]);
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
                    FxPatchEngine.LogErr($"压缩包包含不安全路径，拒绝解压: {entry.FullName}");
                    // 拒绝解压要紧，临时目录删不掉次要——它由 CleanStalePatchTempDirs 兜底，那条路径会记日志。
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

            FxPatchEngine.Log($"已解压补丁包: {Path.GetFileName(zipPath)} → {baseDir}");
            if (jsonEntry is null)
            {
                FxPatchEngine.Log("压缩包内没有 patch.json，改按「整包替换型补丁」识别（需要包里有 _.index.bin 与 bundle 文件）。");
                return null;
            }
            return TryGetSafeChildPath(baseDir, fullName![prefix.Length..], out var patchPath)
                ? patchPath
                : null;
        }
        catch (Exception ex)
        {
            FxPatchEngine.LogErr($"解压补丁包失败: {ex.Message}");
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

    internal static string ResolvePatchFilePath(PatchDef patch, string relativePath)
    {
        var root = patch.BasePath ?? ".";
        if (!TryGetSafeChildPath(root, relativePath, out var path))
            throw new InvalidOperationException($"补丁资源路径不安全: {relativePath}");
        return path;
    }

    /// <summary>清理超过 14 天的旧补丁解压目录（解压目录在数据目录下，生命周期由这里管理而非系统 TEMP）。</summary>
    private static void CleanStalePatchTempDirs(string currentDir)
    {
        try
        {
            var root = Path.GetDirectoryName(currentDir)!;
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var dir in Directory.EnumerateDirectories(root, "poe-toolbox-patch-*"))
            {
                try { if (dir != currentDir && Directory.GetLastWriteTime(dir) < cutoff) Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { FileLogger.App.Warn($"旧补丁解压目录未清理，会在数据目录下继续占空间：{dir}（{ex.Message}）"); }
            }
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn($"扫描旧补丁解压目录失败，本次跳过清理：{ex.Message}");
        }
    }

    private static void ValidatePatch(PatchDef patch)
    {
        if (string.IsNullOrWhiteSpace(patch.PatchId)) throw new InvalidOperationException("缺少 patchId。");
        if (string.IsNullOrWhiteSpace(patch.BundleName)) throw new InvalidOperationException("缺少 bundleName。");
        // version 会被拼进 bundle 路径，写了就必须限制字符集；不写则本补丁固定用 PATCHED/<bundleName>
        if (!string.IsNullOrWhiteSpace(patch.Version))
        {
            if (patch.Version.Contains("..")) throw new InvalidOperationException("version 不能包含 '..'。");
            foreach (var c in patch.Version)
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                    throw new InvalidOperationException($"version 只能包含字母、数字、点、下划线和短横线：\"{patch.Version}\"。");
        }
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

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}
