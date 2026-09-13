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
    /// Ensures the original index is retained before a game-data mutation starts.
    /// </summary>
    public static IndexBackupSession Begin(GameDataAccess gameData)
    {
        var backupDirectory = GetBackupDirectory(gameData.GameDataPath);
        DeleteLegacyJournal(backupDirectory);
        var beforeHash = HashIndex(gameData);
        var baselinePath = GetBaselinePath(gameData.GameDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        var createdBaseline = EnsureBaseline(baselinePath, gameData);
        FileLogger.App.Info($"Backup session started: {backupDirectory} (baseline {(createdBaseline ? "created" : "already existed")}, hash {beforeHash[..12]})");

        return new IndexBackupSession(backupDirectory, baselinePath, beforeHash, createdBaseline);
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
