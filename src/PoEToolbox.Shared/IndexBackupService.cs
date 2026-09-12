using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoEToolbox.Shared;

public sealed record IndexBackupSession(
    string BackupDirectory,
    string BaselinePath,
    string BeforeHash,
    bool CreatedBaseline);

public static class IndexBackupService
{
    private const string BackupScope = "game-data";
    private const string BaselineFileName = "baseline.index.bin";
    private const string JournalFileName = "journal.jsonl";

    /// <summary>
    /// Ensures the original index is retained before a game-data mutation starts.
    /// </summary>
    public static IndexBackupSession Begin(GameDataAccess gameData)
    {
        var backupDirectory = GetBackupDirectory(gameData.GameDataPath);
        Directory.CreateDirectory(backupDirectory);

        var indexBytes = gameData.ReadIndexBytes();
        var beforeHash = Hash(indexBytes);
        var baselinePath = Path.Combine(backupDirectory, BaselineFileName);
        var createdBaseline = EnsureBaseline(baselinePath, indexBytes);
        FileLogger.App.Info($"Backup session started: {backupDirectory} (baseline {(createdBaseline ? "created" : "already existed")}, hash {beforeHash[..12]})");

        return new IndexBackupSession(backupDirectory, baselinePath, beforeHash, createdBaseline);
    }

    /// <summary>Appends an audit entry after a successful mutation has been saved.</summary>
    public static void Complete(
        GameDataAccess gameData,
        IndexBackupSession session,
        string operation,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var afterHash = Hash(gameData.ReadIndexBytes());
        if (afterHash == session.BeforeHash)
            return;

        var entry = new JournalEntry(
            DateTimeOffset.UtcNow,
            gameData.GameDataPath,
            operation,
            session.BeforeHash,
            afterHash,
            parameters);
        var journalPath = Path.Combine(session.BackupDirectory, JournalFileName);
        File.AppendAllText(
            journalPath,
            JsonSerializer.Serialize(entry) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        FileLogger.App.Info($"Mutation complete: {operation} ({session.BeforeHash[..12]} -> {afterHash[..12]})");
    }

    /// <summary>Restores the original index. The game launcher repairs version mismatches on its next update.</summary>
    public static void RestoreBaseline(GameDataAccess gameData)
    {
        var backupDirectory = GetBackupDirectory(gameData.GameDataPath);
        var baselinePath = Path.Combine(backupDirectory, BaselineFileName);
        if (!File.Exists(baselinePath))
            throw new FileNotFoundException("Baseline backup not found.", baselinePath);
        var session = new IndexBackupSession(
            backupDirectory,
            baselinePath,
            Hash(gameData.ReadIndexBytes()),
            CreatedBaseline: false);

        FileLogger.App.Info($"Restoring baseline index from {baselinePath}");
        gameData.WriteIndexBytes(File.ReadAllBytes(baselinePath));
        Complete(gameData, session, "restore-baseline");
    }

    public static string GetBackupDirectory(string gameDataPath)
    {
        var fullPath = Path.GetFullPath(gameDataPath);
        var backupDirectory = Path.Combine(
            ConfigService.BackupDirectory,
            BackupScope,
            Hash(Encoding.UTF8.GetBytes(fullPath)));
        MigrateLegacyBackup(fullPath, backupDirectory);
        return backupDirectory;
    }

    private static void MigrateLegacyBackup(string gameDataPath, string backupDirectory)
    {
        if (Directory.Exists(backupDirectory))
            return;

        var gameDirectory = Path.GetDirectoryName(gameDataPath)
            ?? throw new ArgumentException("Game data path must include a directory.", nameof(gameDataPath));
        var legacyDirectory = Path.Combine(gameDirectory, "backup");
        if (!Directory.Exists(legacyDirectory))
            return;

        foreach (var legacyFile in Directory.EnumerateFiles(legacyDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(legacyDirectory, legacyFile);
            var destination = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
                File.Copy(legacyFile, destination);
        }
    }

    private static bool EnsureBaseline(string baselinePath, byte[] indexBytes)
    {
        if (File.Exists(baselinePath))
            return false;

        var temporaryPath = baselinePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, indexBytes);
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

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record JournalEntry(
        DateTimeOffset TimestampUtc,
        string GameDataPath,
        string Operation,
        string BeforeHash,
        string AfterHash,
        IReadOnlyDictionary<string, string>? Parameters);
}
