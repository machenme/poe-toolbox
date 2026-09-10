using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.PoeCnPatch;

internal sealed record PatchResult(
    string League,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles);

internal sealed record RestoreResult(
    IReadOnlyList<string> RestoredFiles,
    bool UsedBackups);

internal enum PobGame
{
    Poe1,
    Poe2,
}

internal static class PatchService
{
    private const string GlobalTradeHost = "https://www.pathofexile.com/";
    private const string CnTradeHost = "https://poe.game.qq.com/";
    public static bool TryNormalizeSeason(string input, out string season)
    {
        var value = input.Trim();
        if (value.StartsWith("S", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        if (value.Length == 0 || !value.All(char.IsAsciiDigit))
        {
            season = string.Empty;
            return false;
        }

        season = "S" + value;
        return true;
    }

    public static string BuildLeague(string season) => season + "赛季";

    public static bool TryGetTargets(string path, PobGame game, out string[] targets)
    {
        targets = [];
        if (!TryFindTreeTab(path, game, out var treeTabPath))
            return false;

        var classesPath = Path.GetDirectoryName(treeTabPath);
        if (classesPath is null)
            return false;

        targets =
        [
            treeTabPath,
            Path.Combine(classesPath, "TradeQuery.lua"),
            Path.Combine(classesPath, "TradeQueryGenerator.lua"),
        ];
        return targets.All(File.Exists);
    }

    public static PatchResult Apply(string path, PobGame game, string inputSeason)
    {
        if (!TryNormalizeSeason(inputSeason, out var season))
            throw new ArgumentException("赛季必须为 S30 或 30 这样的格式。", nameof(inputSeason));
        if (!TryGetTargets(path, game, out var targets))
            throw new FileNotFoundException("未找到完整的 TreeTab.lua、TradeQuery.lua 和 TradeQueryGenerator.lua。", path);

        var league = BuildLeague(season);
        var updates = new List<(string Target, string Original, string Updated)>();
        foreach (var target in targets)
        {
            var original = ReadText(target);
            var updated = Path.GetFileName(target).Equals("TreeTab.lua", StringComparison.OrdinalIgnoreCase)
                ? PatchTreeTab(original, league)
                : ReplaceTradeLinks(original);
            if (updated != original)
                updates.Add((target, original, updated));
        }

        // Complete all parsing before changing any target. Each backup is retained for Restore.
        var changedFiles = new List<string>();
        var backupFiles = new List<string>();
        try
        {
            foreach (var update in updates)
            {
                var backupPath = GetBackupPath(update.Target);
                if (!File.Exists(backupPath))
                    File.Copy(update.Target, backupPath);
                backupFiles.Add(backupPath);
            }

            foreach (var update in updates)
            {
                WriteTextAtomically(update.Target, update.Updated);
                changedFiles.Add(update.Target);
            }
        }
        catch
        {
            foreach (var update in updates.Where(update => changedFiles.Contains(update.Target)))
            {
                try { WriteTextAtomically(update.Target, update.Original); }
                catch { }
            }
            throw;
        }

        return new PatchResult(league, changedFiles, backupFiles);
    }

    public static RestoreResult Restore(string path, PobGame game)
    {
        if (!TryGetTargets(path, game, out var targets))
            throw new FileNotFoundException("未找到完整的 TreeTab.lua、TradeQuery.lua 和 TradeQueryGenerator.lua。", path);

        var backupUpdates = targets
            .Select(target => (Target: target, BackupPath: GetBackupPath(target)))
            .Where(item => File.Exists(item.BackupPath))
            .Select(item => (item.Target, item.BackupPath, Updated: ReadText(item.BackupPath)))
            .ToList();
        var restoredFiles = new List<string>();
        if (backupUpdates.Count > 0)
        {
            var originals = backupUpdates.ToDictionary(item => item.Target, item => ReadText(item.Target));
            try
            {
                foreach (var update in backupUpdates)
                {
                    WriteTextAtomically(update.Target, update.Updated);
                    restoredFiles.Add(update.Target);
                }
            }
            catch
            {
                foreach (var target in restoredFiles)
                {
                    try { WriteTextAtomically(target, originals[target]); }
                    catch { }
                }
                throw;
            }

            foreach (var update in backupUpdates)
                File.Delete(update.BackupPath);

            return new RestoreResult(restoredFiles, true);
        }

        foreach (var target in targets)
        {
            var original = ReadText(target);
            var updated = original.Replace(CnTradeHost, GlobalTradeHost, StringComparison.Ordinal);
            if (updated == original)
                continue;

            WriteTextAtomically(target, updated);
            restoredFiles.Add(target);
        }

        return new RestoreResult(restoredFiles, false);
    }

    private static bool TryFindTreeTab(string path, PobGame game, out string treeTabPath)
    {
        treeTabPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (File.Exists(path))
        {
            if (!Path.GetFileName(path).Equals("TreeTab.lua", StringComparison.OrdinalIgnoreCase))
                return false;

            treeTabPath = path;
            return true;
        }

        if (!Directory.Exists(path))
            return false;

        var variantDirectory = Path.Combine(path, game == PobGame.Poe2 ? "PoE2" : "PoE1");
        var searchRoot = Directory.Exists(variantDirectory) ? variantDirectory : path;
        var relativePaths = new[]
        {
            Path.Combine("Classes", "TreeTab.lua"),
            "TreeTab.lua",
        };

        foreach (var relativePath in relativePaths)
        {
            var candidate = Path.Combine(searchRoot, relativePath);
            if (!File.Exists(candidate))
                continue;

            treeTabPath = candidate;
            return true;
        }

        try
        {
            treeTabPath = Directory.EnumerateFiles(searchRoot, "TreeTab.lua", SearchOption.AllDirectories)
                .FirstOrDefault(candidate => Path.GetFileName(candidate).Equals("TreeTab.lua", StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
            return treeTabPath.Length > 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PatchTreeTab(string text, string league)
    {
        var updated = ReplaceTradeLinks(text);
        var newline = GetNewline(updated);
        var leagueList = BuildLeagueList(league, newline);
        if (updated.Contains("local encodedLeague = s_gsub(selectedLeague", StringComparison.Ordinal))
        {
            updated = ReplaceGeneratedLeagueList(updated, leagueList);
            return Regex.Replace(
                updated,
                "(?m)^(\\s*local selectedLeague = controls\\.searchTradeLeagueSelect:GetSelValue\\(\\) or )[\\\"'][^\\\"']*[\\\"']",
                "$1\"" + league + "\"");
        }

        const string oldList = "\tself.tradeLeaguesList = {}";
        const string oldRealm = "\t\tlocal realmPath = selectedRealm == \"pc\" and \"\" or (selectedRealm .. \"/\")";
        const string oldLeague = "\t\t\t(controls.searchTradeLeagueSelect:GetSelValue()) ..";
        var newRealm = string.Join(newline,
            $"\t\tlocal selectedLeague = controls.searchTradeLeagueSelect:GetSelValue() or \"{league}\"",
            "\t\tlocal encodedLeague = s_gsub(selectedLeague, \"[^a-zA-Z0-9]\", function(a)",
            "\t\t\treturn s_format(\"%%%02X\", s_byte(a))",
            "\t\tend)",
            oldRealm);

        if (updated.Contains(oldList, StringComparison.Ordinal))
            updated = updated.Replace(oldList, leagueList, StringComparison.Ordinal);
        else
            updated = ReplaceGeneratedLeagueList(updated, leagueList);

        RequireContains(updated, oldRealm, "TreeTab.lua 中未找到交易站 URL 设置。");
        RequireContains(updated, oldLeague, "TreeTab.lua 中未找到交易站赛季 URL。");
        return updated
            .Replace(oldRealm, newRealm, StringComparison.Ordinal)
            .Replace(oldLeague, "\t\t\tencodedLeague ..", StringComparison.Ordinal);
    }

    private static string ReplaceTradeLinks(string text)
        => text.Replace(GlobalTradeHost, CnTradeHost, StringComparison.Ordinal);

    private static string BuildLeagueList(string league, string newline)
    {
        return string.Join(newline,
            "\tself.tradeLeaguesList = {",
            $"\t\tpc = {{ \"{league}\" }},",
            $"\t\tsony = {{ \"{league}\" }},",
            $"\t\txbox = {{ \"{league}\" }},",
            "\t}");
    }

    private static string ReplaceGeneratedLeagueList(string text, string replacement)
    {
        const string pattern = "(?ms)^[\\t ]*self\\.tradeLeaguesList\\s*=\\s*\\{\\s*^[\\t ]*pc\\s*=\\s*\\{[^}]*\\},\\s*^[\\t ]*sony\\s*=\\s*\\{[^}]*\\},\\s*^[\\t ]*xbox\\s*=\\s*\\{[^}]*\\},\\s*^[\\t ]*\\}";
        return new Regex(pattern).Replace(text, replacement, 1);
    }

    private static string ReadText(string path) => File.ReadAllText(path, new UTF8Encoding(false, true));

    private static string GetBackupPath(string target)
    {
        var fullPath = Path.GetFullPath(target);
        var identifier = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        var backupDirectory = Path.Combine(
            ConfigService.BackupDirectory,
            "pob-cn-patch",
            identifier);
        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(target) + ".bak");
        if (File.Exists(backupPath))
            return backupPath;

        Directory.CreateDirectory(backupDirectory);
        var legacyPath = target + ".bak";
        if (!File.Exists(legacyPath))
            return backupPath;

        try
        {
            File.Move(legacyPath, backupPath);
            return backupPath;
        }
        catch (IOException)
        {
            return legacyPath;
        }
    }

    private static void WriteTextAtomically(string path, string text)
    {
        var temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string GetNewline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static void RequireContains(string text, string expected, string message)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }
}
