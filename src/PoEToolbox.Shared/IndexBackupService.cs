using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PoEToolbox.Shared;

public sealed record IndexBackupSession(
    string BackupDirectory,
    string BaselinePath,
    string BeforeHash,
    bool CreatedBaseline);

public static class IndexBackupService
{
    private const string BackupScope = "game-data";
    private const string BackupDirectoryName = "backup";
    private const string BaselineFileName = "_.index.bin";
    private const string LegacyBaselineFileName = "baseline.index.bin";
    /// <summary>
    /// Legacy audit log (see <see cref="DeleteLegacyJournal"/>): it recorded index hashes only and was
    /// never used to restore anything, so it is no longer written.
    /// </summary>
    private const string LegacyJournalFileName = "journal.jsonl";

    /// <summary>Chunk size for hashing and copying the index; the whole file is never held in memory.</summary>
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>
    /// 游戏更新检测：客户端干净（无任何已应用补丁、PATCHED 目录无 bundle）且当前索引与基线不一致时，
    /// 基线已过期（游戏更新所致）——自动把基线刷新为当前索引，避免之后任何「恢复原版/还原基线」
    /// 把旧版文件写回新客户端。客户端有补丁时不动：此时索引与基线的差异来自补丁，属正常。
    /// 差异也可能来自外部工具改动；客户端干净的前提下接受其为新基线（外部改动本就不在本工具管辖内）。
    /// </summary>
    public static void RefreshBaselineIfStale(string gameDataPath)
    {
        try
        {
            var resolved = GameDataAccess.ResolvePath(gameDataPath);
            var baselinePath = GetBaselinePath(resolved);
            if (!File.Exists(baselinePath))
                return; // EnsureBaselineExists 负责首次创建
            if (FilesEqual(resolved, baselinePath))
                return; // 索引与基线一致，无需处理
            if (!IsClientClean(Path.GetDirectoryName(resolved)!))
            {
                FileLogger.App.Info("游戏索引与原始基线不一致，但客户端存在已应用补丁：基线保持不变。");
                return;
            }
            if (!IndexHasNoPatchBundleRecords(resolved))
            {
                // 磁盘上看是"干净"（账本空、PATCHED 无文件），但索引仍引用 PATCHED bundle = 悬空状态；
                // 把它刷进基线等于把悬空固化成"原版"，之后恢复原版永远修不回来。
                FileLogger.App.Info("游戏索引仍引用 PATCHED 补丁 bundle（悬空状态）：基线保持不变。");
                return;
            }
            File.Copy(resolved, baselinePath, overwrite: true);
            FileLogger.App.Info(
                $"游戏索引已更新且客户端无补丁：原始索引基线已刷新为当前版本（{new FileInfo(resolved).Length:N0} bytes）。");
        }
        catch (Exception ex)
        {
            FileLogger.App.Error("检查/刷新原始索引基线失败（不影响游戏数据）。", ex);
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        var fileA = new FileInfo(a);
        var fileB = new FileInfo(b);
        if (fileA.Length != fileB.Length)
            return false;
        using var streamA = fileA.OpenRead();
        using var streamB = fileB.OpenRead();
        var bufferA = new byte[CopyBufferSize];
        var bufferB = new byte[CopyBufferSize];
        int read;
        while ((read = streamA.Read(bufferA, 0, bufferA.Length)) > 0)
        {
            if (streamB.Read(bufferB, 0, bufferB.Length) != read)
                return false;
            for (var i = 0; i < read; i++)
            {
                if (bufferA[i] != bufferB[i])
                    return false;
            }
        }
        return streamB.Read(bufferB, 0, 1) == 0;
    }

    /// <summary>客户端是否处于「无补丁」状态：补丁账本为空/缺失，且 PATCHED 目录没有 bundle。</summary>
    private static bool IsClientClean(string indexDirectory)
    {
        var gameDataPath = Path.Combine(indexDirectory, "_.index.bin");
        if (FxPatchStateStore.Read(gameDataPath).Count > 0)
            return false;
        var patchedDirectory = Path.Combine(indexDirectory, "PATCHED");
        return !Directory.Exists(patchedDirectory)
            || !Directory.EnumerateFiles(patchedDirectory, "*.bundle.bin").Any();
    }

    /// <summary>索引记录层面没有任何 PATCHED bundle 引用。IsClientClean 只看磁盘证据，
    /// 看不出「PATCHED 文件已丢但索引仍引用」的悬空状态——那种状态下刷新基线会把悬空固化。</summary>
    private static bool IndexHasNoPatchBundleRecords(string resolvedIndex)
    {
        try
        {
            using var gd = GameDataAccess.OpenReadOnlyMapped(resolvedIndex);
            return !HasPatchBundleRecord(gd.Index);
        }
        catch (Exception ex)
        {
            FileLogger.App.Error($"打开索引核对补丁引用失败，基线保持不变：{ex.Message}");
            return false;
        }
        finally
        {
            // Opened outside GameDataLoader, so the reclaim has to be requested here as well.
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }
    }

    /// <summary>
    /// Ensures the original index is retained before a game-data mutation starts.
    /// </summary>
    public static IndexBackupSession Begin(GameDataAccess gameData)
    {
        var backupDirectory = GetBackupDirectory(gameData.GameDataPath);
        DeleteLegacyJournal(backupDirectory);
        var beforeHash = HashIndex(gameData);
        var baselinePath = GetBaselinePath(gameData.GameDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        // 基线写入后会被之后所有「恢复原版」当官方索引整体回写：在补丁已应用时创建基线，
        // 等于把 PATCHED 重定向固化成"原版"——恢复后索引指向已被清理的补丁文件，游戏与工具全部悬空
        //（2026-09-16 词缀上色悬空 v12 bundle 即此成因）。宁缺毋滥：宁可不建，也不能建脏的。
        var createdBaseline = IndexIsPatchFree(gameData.Index) && EnsureBaseline(baselinePath, gameData);
        if (!createdBaseline && !File.Exists(baselinePath))
            FileLogger.App.Warn(
                "当前索引仍引用 PATCHED/ 补丁 bundle，已跳过基线创建：把打补丁的索引存成「原版基线」，"
                + "之后每次恢复原版都会指向丢失的补丁文件。请先执行「恢复游戏原版」或用启动器验证游戏文件，"
                + "得到干净索引后基线会自动建立。");
        FileLogger.App.Info($"Backup session started: {backupDirectory} (baseline {(createdBaseline ? "created" : "already existed")}, hash {beforeHash[..12]})");

        return new IndexBackupSession(backupDirectory, baselinePath, beforeHash, createdBaseline);
    }

    /// <summary>索引是否已与补丁脱钩：没有任何 bundle 记录落在 PATCHED/ 目录下。</summary>
    private static bool IndexIsPatchFree(LibBundle3.Index index) => !HasPatchBundleRecord(index);

    /// <summary>索引记录里是否存在指向 PATCHED/ 目录的 bundle。</summary>
    private static bool HasPatchBundleRecord(LibBundle3.Index index)
    {
        foreach (var br in index.Bundles.Span)
        {
            if (br.Path.StartsWith("PATCHED/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Logs a mutation after it has been saved. No-op saves are skipped, detected by hash.</summary>
    public static void Complete(
        GameDataAccess gameData,
        IndexBackupSession session,
        string operation,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var afterHash = HashIndex(gameData);
        if (afterHash == session.BeforeHash)
            return;

        var details = parameters is { Count: > 0 }
            ? " | " + string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))
            : string.Empty;
        FileLogger.App.Info(
            $"Mutation complete: {operation} ({session.BeforeHash[..12]} -> {afterHash[..12]}){details}");
    }

    /// <summary>Restores the original index. The game launcher repairs version mismatches on its next update.</summary>
    public static void RestoreBaseline(GameDataAccess gameData)
    {
        var backupDirectory = GetBackupDirectory(gameData.GameDataPath);
        var baselinePath = GetBaselinePath(gameData.GameDataPath);
        if (!File.Exists(baselinePath))
            throw new FileNotFoundException("Baseline backup not found.", baselinePath);
        var session = new IndexBackupSession(
            backupDirectory,
            baselinePath,
            HashIndex(gameData),
            CreatedBaseline: false);

        FileLogger.App.Info($"Restoring baseline index from {baselinePath}");
        gameData.WriteIndexBytesFrom(baselinePath);
        // 回到原版索引 = 之前打的补丁全都没了，账本要一并清空，否则界面还会显示它们已启用。
        FxPatchStateStore.Clear(gameData.GameDataPath);
        Complete(gameData, session, "restore-baseline");
    }

    public static string GetBackupDirectory(string gameDataPath)
    {
        var fullPath = Path.GetFullPath(gameDataPath);
        var gameDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Game data path must include a directory.", nameof(gameDataPath));
        return Path.Combine(gameDirectory, BackupDirectoryName);
    }

    /// <summary>Returns the retained baseline index path for a game-data path.</summary>
    public static string GetBaselinePath(string gameDataPath)
    {
        var fullPath = Path.GetFullPath(gameDataPath);
        var baselinePath = Path.Combine(GetBackupDirectory(fullPath), BaselineFileName);
        MigrateLegacyBaseline(fullPath, baselinePath);
        return baselinePath;
    }

    /// <summary>
    /// Returns the original index backup, creating it from the current game index when it is absent.
    /// Call this before an operation that needs an original-index snapshot but does not otherwise mutate data.
    /// </summary>
    /// <param name="gameDataPath">A Content.ggpk, an _.index.bin, or a directory holding one of them.</param>
    /// <param name="openContainerIfNeeded">
    /// When false, a GGPK client is left closed instead of being opened just to copy its index out
    /// (opening one costs a full load). Bundles2 installs are always handled, they only need a file copy.
    /// </param>
    /// <returns>The baseline path, or null when it is missing and was not created.</returns>
    public static string? EnsureBaselineExists(string gameDataPath, bool openContainerIfNeeded = true)
    {
        var resolvedGameData = GameDataAccess.ResolvePath(gameDataPath);
        var baselinePath = GetBaselinePath(resolvedGameData);
        if (File.Exists(baselinePath))
            return baselinePath;

        // A Bundles2 index is a plain file next to its bundles: copy it directly instead of loading a
        // full index only to stream the same bytes back out.
        if (!resolvedGameData.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            var created = CopyFileIntoPlace(resolvedGameData, baselinePath);
            FileLogger.App.Info(
                $"Original index backup {(created ? "created" : "already existed")}: {baselinePath}");
            return baselinePath;
        }

        if (!openContainerIfNeeded)
            return null;

        try
        {
            using var gameData = GameDataAccess.OpenReadOnlyMapped(resolvedGameData);
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            var created = EnsureBaseline(baselinePath, gameData);
            FileLogger.App.Info(
                $"Original index backup {(created ? "created" : "already existed")}: {baselinePath}");
            return baselinePath;
        }
        finally
        {
            // Opened outside GameDataLoader (the patch plugin asks for the baseline on startup),
            // so the reclaim has to be requested here too.
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }
    }

    private static void MigrateLegacyBaseline(string gameDataPath, string baselinePath)
    {
        if (File.Exists(baselinePath))
            return;

        var gameDirectory = Path.GetDirectoryName(gameDataPath)!;
        var candidates = new[]
        {
            Path.Combine(gameDirectory, BackupDirectoryName, "raw", BaselineFileName),
            Path.Combine(gameDirectory, BackupDirectoryName, LegacyBaselineFileName),
            Path.Combine(ConfigService.BackupDirectory, BackupScope, Hash(Encoding.UTF8.GetBytes(gameDataPath)), LegacyBaselineFileName),
        };
        var legacyPath = candidates.FirstOrDefault(File.Exists);
        if (legacyPath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        File.Copy(legacyPath, baselinePath);
        DeleteLegacyJournal(Path.GetDirectoryName(legacyPath)!);
        FileLogger.App.Info($"Migrated legacy index backup to {baselinePath}");
    }

    /// <summary>
    /// Writes the current index as the retained baseline, streamed from the game data rather than
    /// loaded first: the baseline is written once but on every session where it is still missing.
    /// </summary>
    private static bool EnsureBaseline(string baselinePath, GameDataAccess gameData)
    {
        if (File.Exists(baselinePath))
            return false;

        var temporaryPath = baselinePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, CopyBufferSize))
            {
                CopyIndex(gameData, target);
            }

            try
            {
                File.Move(temporaryPath, baselinePath);
                return true;
            }
            catch (IOException) when (File.Exists(baselinePath))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> through a temporary file, so a
    /// crash or a second instance racing on the same target can never leave a half-written backup behind.
    /// Returns false when the destination was already there (written by whoever got there first).
    /// </summary>
    private static bool CopyFileIntoPlace(string source, string destination)
    {
        var temporaryPath = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferSize))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                input.CopyTo(output, CopyBufferSize);
            }

            try
            {
                File.Move(temporaryPath, destination);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// SHA-256 of the raw index, computed block by block instead of over a materialised byte array.
    /// This is the hash used to tell whether a mutation actually changed anything, so it runs on every
    /// save and must stay cheap.
    /// </summary>
    private static string HashIndex(GameDataAccess gameData)
    {
        using var reader = gameData.OpenIndexReader();
        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = reader.Read(buffer)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexStringLower(sha.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Copies the raw index into <paramref name="target"/> without loading it into memory.</summary>
    private static void CopyIndex(GameDataAccess gameData, Stream target)
    {
        using var reader = gameData.OpenIndexReader();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = reader.Read(buffer)) > 0)
                target.Write(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Removes the audit log written by older versions. It only ever held before/after index hashes and was
    /// never read back, so restoring has always depended solely on the baseline index.
    /// </summary>
    private static void DeleteLegacyJournal(string backupDirectory)
    {
        try
        {
            var journalPath = Path.Combine(backupDirectory, LegacyJournalFileName);
            if (!File.Exists(journalPath))
                return;

            File.Delete(journalPath);
            FileLogger.App.Info($"Removed legacy mutation journal: {journalPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.App.Warn($"Could not remove legacy mutation journal in {backupDirectory}: {ex.Message}");
        }
    }
}
