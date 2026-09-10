using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using PoEToolbox.Core.Binary.Datc64;
using PoEToolbox.Shared;

namespace PoEToolbox.Core.Pipeline;

/// <summary>
/// Matches poe.ninja exchange category data to BaseItemTypes rows
/// and applies price tags to item names.
/// </summary>
public static class CategoryPriceTagger
{
    // PoE1 data paths
    public const string PoE1_EnPath = "data/baseitemtypes.datc64";
    public const string PoE1_TcPath = "data/traditional chinese/baseitemtypes.datc64";
    // PoE2 data paths
    public const string PoE2_EnPath = "data/balance/baseitemtypes.datc64";
    public const string PoE2_TcPath = "data/balance/traditional chinese/baseitemtypes.datc64";
    // Strip existing price suffix: " 2 C", " [ 2c ]", " [ 1.5d ]", etc.
    private static readonly Regex PriceTail = new(
        @"\s*\[\s*\d+(?:\.\d+)?\s*[edc]\s*\]$|\s+\d+(?:\.\d+)?\s*[EDCedc]$",
        RegexOptions.Compiled);
    // poe.ninja uses IDs such as "uncut-skill-gem-20", while the client uses
    // "Uncut Skill Gem (Level 20)".
    private static readonly Regex LevelQualifiedItemId = new(
        @"^(uncut(?:skill|spirit|support)gem|thaumaturgicflux)(\d+)$",
        RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> PriceItemAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alch"] = "orbofalchemy",
            ["annul"] = "orbofannulment",
            ["artificers"] = "artificersorb",
            ["aug"] = "orbofaugmentation",
            ["bauble"] = "glassblowersbauble",
            ["chance"] = "orbofchance",
            ["chaos"] = "chaosorb",
            ["divine"] = "divineorb",
            ["etcher"] = "arcanistsetcher",
            ["exalted"] = "exaltedorb",
            ["gcp"] = "gemcuttersprism",
            ["mirror"] = "mirrorofkalandra",
            ["regal"] = "regalorb",
            ["scrap"] = "armourersscrap",
            ["transmute"] = "orboftransmutation",
            ["vaal"] = "vaalorb",
            ["whetstone"] = "blacksmithswhetstone",
            ["wisdom"] = "scrollofwisdom",
            ["againstthedarkness"] = "zarokhsreliquarykeyagainstthedarkness",
        };

    /// <summary>Items whose names should never be modified</summary>
    private static readonly HashSet<string> PriceExceptions = ["神聖石", "混沌石", "崇高石"];

    public record ExchangePrices(
        string PrimaryCurrency,
        string SecondaryCurrency,
        double SecondaryPerPrimary,
        Dictionary<string, double> Prices);

    public record TagResult(int Matched, int Tagged, int Skipped);

    // ═══ Embedded name dictionaries ═══════════════════════════

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Poe1Names =
        new(() => LoadEmbeddedNameDictionary("poe1_base_item_names.json"));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Poe2Names =
        new(() => LoadEmbeddedNameDictionary("poe2_base_item_names.json"));
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, int>> ClientRowIndices = new();

    /// <summary>
    /// Builds an English item slug to Traditional Chinese name dictionary for release packaging.
    /// Row indices are intentionally not stored because game client updates can change them.
    /// </summary>
    public static Dictionary<string, string> BuildEmbeddedNameDictionary(string gameDataPath)
    {
        using var gd = GameDataAccess.Open(gameDataPath, readOnly: true);
        var tcKey = gd.IsPoe2Client ? PoE2_TcPath : PoE1_TcPath;
        var enKey = gd.IsPoe2Client ? PoE2_EnPath : PoE1_EnPath;

        if (!gd.Index.TryGetFile(tcKey, out var tcFr))
            throw new FileNotFoundException($"Not found: {tcKey}");
        if (!gd.Index.TryGetFile(enKey, out var enFr))
            throw new FileNotFoundException($"Not found: {enKey}");

        var (tcIs64, tcVf) = Datc64File.DetectFromExtension(tcKey);
        var tcFile = Datc64File.FromBytes(tcFr.Read().ToArray(), "BaseItemTypes", tcIs64, tcVf);
        var (enIs64, enVf) = Datc64File.DetectFromExtension(enKey);
        var enFile = Datc64File.FromBytes(enFr.Read().ToArray(), "BaseItemTypes", enIs64, enVf);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Min(tcFile.Rows.Count, enFile.Rows.Count); i++)
        {
            var englishName = enFile.Rows[i].GetValueOrDefault("Name") as string ?? string.Empty;
            var chineseName = CleanName(tcFile.Rows[i].GetValueOrDefault("Name") as string ?? string.Empty);
            var slug = Slugify(englishName);
            if (slug.Length > 0 && chineseName.Length > 0)
                names.TryAdd(slug, chineseName);
        }

        return names;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedNameDictionary(string fileName)
    {
        var resourceName = $"PoEToolbox.Core.Assets.NameDictionaries.{fileName}";
        using var stream = typeof(CategoryPriceTagger).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded name dictionary missing: {fileName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Embedded name dictionary is invalid: {fileName}");
    }

    // ═══ Run ═══════════════════════════════════════════════════

    /// <summary>
    /// Run price tagging for a single poe.ninja category against a GGPK file.
    /// </summary>
    public static (int matched, int tagged, int skipped) Run(
        string ggpkPath,
        string category,
        string league,
        string poeNinjaDir = "work/poe_ninja",
        string workDir = "work",
        bool dryRun = false,
        bool skipBackup = false,
        IProgress<string>? progress = null)
    {
        var result = RunMany(ggpkPath, [category], poeNinjaDir, workDir, dryRun, skipBackup, progress);
        if (!result.TryGetValue(category, out var categoryResult))
            return (0, 0, 0);
        return (categoryResult.Matched, categoryResult.Tagged, categoryResult.Skipped);
    }

    /// <summary>
    /// Applies prices for multiple categories in one client-data session.
    /// The client is opened, backed up, parsed and written only once.
    /// </summary>
    public static IReadOnlyDictionary<string, TagResult> RunMany(
        string ggpkPath,
        IReadOnlyList<string> categories,
        string poeNinjaDir = "work/poe_ninja",
        string workDir = "work",
        bool dryRun = false,
        bool skipBackup = false,
        IProgress<string>? progress = null)
    {
        var exchanges = new Dictionary<string, ExchangePrices>(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, TagResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            try
            {
                var jsonPath = Path.Combine(poeNinjaDir, $"{category}.json");
                if (!File.Exists(jsonPath))
                    throw new FileNotFoundException($"poe.ninja data not found: {jsonPath}");

                var exchange = ParseExchangeJson(jsonPath);
                exchanges.Add(category, exchange);
                progress?.Report($"── {category} ──");
                progress?.Report($"    Primary: {exchange.PrimaryCurrency}  |  {exchange.Prices.Count} items");
            }
            catch (Exception ex)
            {
                results[category] = new TagResult(0, 0, 0);
                progress?.Report($"── {category} ── Failed: {ex.Message}");
            }
        }

        if (exchanges.Count == 0)
            return results;

        progress?.Report("[1] Opening game data...");
        using var gd = GameDataAccess.Open(ggpkPath);
        progress?.Report($"    Opened ({(gd.IsBundles2 ? "Bundles2" : "GGPK")}).");

        var isPoe2 = gd.IsPoe2Client;
        var tcKey = isPoe2 ? PoE2_TcPath : PoE1_TcPath;
        var enKey = isPoe2 ? PoE2_EnPath : PoE1_EnPath;
        if (!gd.Index.TryGetFile(tcKey, out var tcFr))
            throw new FileNotFoundException($"Not found: {tcKey}");
        if (!gd.Index.TryGetFile(enKey, out var enFr))
            throw new FileNotFoundException($"Not found: {enKey}");

        progress?.Report("[2] Reading BaseItemTypes...");
        var origBytes = tcFr.Read().ToArray();
        var (tcIs64, tcVf) = Datc64File.DetectFromExtension(tcKey);
        var tcFile = Datc64File.FromBytes(origBytes, "BaseItemTypes", tcIs64, tcVf);
        var names = isPoe2 ? Poe2Names.Value : Poe1Names.Value;
        var rowIndices = GetClientRowIndices(isPoe2, enKey, enFr.Read().ToArray());
        progress?.Report($"    {tcFile.Count} rows parsed; embedded {(isPoe2 ? "PoE2" : "PoE1")} dictionary loaded.");

        var modifiedRows = new HashSet<int>();
        foreach (var (category, exchange) in exchanges)
        {
            var lookup = BuildRuntimeLookup(rowIndices, tcFile, names, exchange.Prices.Keys);
            var tagged = 0;
            var matched = 0;
            var skipped = 0;
            var primaryIsDivine = exchange.PrimaryCurrency.Equals("divine", StringComparison.OrdinalIgnoreCase);

            foreach (var (slug, price) in exchange.Prices)
            {
                var key = NormalizePriceItemId(slug);
                if (!lookup.TryGetValue(key, out var entry))
                    continue;

                matched++;
                if (PriceExceptions.Contains(entry.CleanName) || !modifiedRows.Add(entry.RowIndex))
                {
                    skipped++;
                    continue;
                }

                string tag;
                if (primaryIsDivine)
                {
                    if (price >= 1.0)
                    {
                        tag = $" [ {Math.Round(price, 1):F1}d ]";
                    }
                    else
                    {
                        var secondaryValue = price * exchange.SecondaryPerPrimary;
                        if (secondaryValue < 1.0)
                        {
                            skipped++;
                            modifiedRows.Remove(entry.RowIndex);
                            continue;
                        }
                        tag = $" [ {Math.Round(secondaryValue, 1):F1}e ]";
                    }
                }
                else
                {
                    if (price < 1.0)
                    {
                        skipped++;
                        modifiedRows.Remove(entry.RowIndex);
                        continue;
                    }

                    var divineValue = price * exchange.SecondaryPerPrimary;
                    tag = divineValue >= 1.0
                        ? $" [ {Math.Round(divineValue, 1):F1}d ]"
                        : $" [ {Math.Round(price, 1):F1}c ]";
                }

                if ($"{entry.CleanName}{tag}".Length > 100)
                {
                    skipped++;
                    modifiedRows.Remove(entry.RowIndex);
                    continue;
                }

                tcFile.Rows[entry.RowIndex]["Name"] = $"{entry.CleanName}{tag}";
                tagged++;
                if (tagged <= 10)
                    progress?.Report($"  {entry.CleanName}{tag}");
            }

            if (tagged > 10)
                progress?.Report($"  ... and {tagged - 10} more");
            results[category] = new TagResult(matched, tagged, skipped);
            progress?.Report($"  {category}: matched {matched}, tagged {tagged}, skipped {skipped}");
        }

        var totalTagged = results.Values.Sum(result => result.Tagged);
        if (totalTagged == 0)
        {
            progress?.Report("    Nothing to write.");
            return results;
        }

        if (dryRun)
        {
            progress?.Report("[3] DRY RUN — no changes written.");
            return results;
        }

        progress?.Report("[3] Encoding...");
        var bin = tcFile.ToBytes();
        try
        {
            var verify = Datc64File.FromBytes(bin, "BaseItemTypes", tcIs64, tcVf);
            if (verify.Count != tcFile.Count)
            {
                progress?.Report($"    WARNING: row count mismatch ({verify.Count} vs {tcFile.Count}) — skipping write.");
                return results.ToDictionary(pair => pair.Key, pair => pair.Value with { Tagged = 0 });
            }
            progress?.Report("    Round-trip OK.");
        }
        catch (Exception ex)
        {
            progress?.Report($"    WARNING: round-trip failed ({ex.Message}) — skipping write.");
            return results.ToDictionary(pair => pair.Key, pair => pair.Value with { Tagged = 0 });
        }

        progress?.Report("    Writing back...");
        var backup = IndexBackupService.Begin(gd);
        try
        {
            tcFr.Write(bin);
            gd.Save();

            // Re-open the modified payload through the same parser before reporting success.
            var savedBytes = gd.ReadFile(tcKey)
                ?? throw new InvalidDataException($"Modified file disappeared: {tcKey}");
            var saved = Datc64File.FromBytes(savedBytes, "BaseItemTypes", tcIs64, tcVf);
            if (saved.Count != tcFile.Count)
                throw new InvalidDataException($"Saved row count mismatch ({saved.Count} vs {tcFile.Count}).");

            try
            {
                IndexBackupService.Complete(gd, backup, "price-tag", new Dictionary<string, string>
                {
                    ["game"] = isPoe2 ? "poe2" : "poe1",
                    ["categories"] = string.Join(",", exchanges.Keys),
                });
            }
            catch (Exception ex)
            {
                // The data mutation succeeded; expose journal failure without pretending the write failed.
                progress?.Report($"    WARNING: journal write failed ({ex.Message}).");
            }
        }
        catch
        {
            try
            {
                tcFr.Write(origBytes);
                gd.Save();
                progress?.Report("    Write failed; original BaseItemTypes content was restored.");
            }
            catch (Exception restoreEx)
            {
                progress?.Report($"    CRITICAL: write failed and restore also failed ({restoreEx.Message}).");
            }
            throw;
        }
        progress?.Report("    Done.");
        return results;
    }

    // ═══ Parse ═════════════════════════════════════════════════

    public static ExchangePrices ParseExchangeJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var core = root.GetProperty("core");
        var primaryCurrency = core.GetProperty("primary").GetString() ?? "chaos";
        var secondaryCurrency = core.GetProperty("secondary").GetString() ?? string.Empty;
        var rates = core.GetProperty("rates");
        var secondaryRate = rates.TryGetProperty(secondaryCurrency, out var rate)
            ? rate.GetDouble()
            : 0;

        var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            var id = line.GetProperty("id").GetString();
            var pv = line.TryGetProperty("primaryValue", out var pvEl) ? pvEl.GetDouble() : 0;
            if (id is not null) prices[id] = pv;
        }
        return new ExchangePrices(primaryCurrency, secondaryCurrency, secondaryRate, prices);
    }

    private static IReadOnlyDictionary<string, int> GetClientRowIndices(
        bool isPoe2, string englishTablePath, byte[] englishTableBytes)
    {
        var fingerprint = $"{(isPoe2 ? "poe2" : "poe1")}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(englishTableBytes))}";
        return ClientRowIndices.GetOrAdd(fingerprint, _ =>
        {
            var (is64, validFor) = Datc64File.DetectFromExtension(englishTablePath);
            var englishFile = Datc64File.FromBytes(englishTableBytes, "BaseItemTypes", is64, validFor);
            var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < englishFile.Rows.Count; i++)
            {
                var slug = Slugify(englishFile.Rows[i].GetValueOrDefault("Name") as string ?? string.Empty);
                if (slug.Length > 0)
                    indices.TryAdd(slug, i);
            }
            return indices;
        });
    }

    private static Dictionary<string, (int RowIndex, string CleanName)> BuildRuntimeLookup(
        IReadOnlyDictionary<string, int> rowIndices,
        Datc64File traditionalChineseFile,
        IReadOnlyDictionary<string, string> embeddedNames,
        IEnumerable<string> priceItemIds)
    {
        var lookup = new Dictionary<string, (int RowIndex, string CleanName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemId in priceItemIds)
        {
            var slug = NormalizePriceItemId(itemId);
            if (!rowIndices.TryGetValue(slug, out var rowIndex)
                || rowIndex >= traditionalChineseFile.Rows.Count)
                continue;

            var fallbackName = CleanName(
                traditionalChineseFile.Rows[rowIndex].GetValueOrDefault("Name") as string ?? string.Empty);
            var name = embeddedNames.TryGetValue(slug, out var embeddedName)
                ? embeddedName
                : fallbackName;
            if (name.Length > 0)
                lookup.TryAdd(slug, (rowIndex, name));
        }
        return lookup;
    }

    // ═══ Helpers ═══════════════════════════════════════════════

    /// <summary>Normalize to lowercase alphanumeric only (no dashes, spaces, etc.)</summary>
    private static string Slugify(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var chars = new char[normalized.Length];
        var j = 0;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c is >= 'a' and <= 'z') chars[j++] = c;
            else if (c is >= 'A' and <= 'Z') chars[j++] = (char)(c + 32);
            else if (c is >= '0' and <= '9') chars[j++] = c;
        }
        return new string(chars, 0, j);
    }

    private static string NormalizePriceItemId(string itemId)
    {
        var slug = Slugify(itemId);
        var match = LevelQualifiedItemId.Match(slug);
        if (match.Success)
            slug = $"{match.Groups[1].Value}level{match.Groups[2].Value}";
        return PriceItemAliases.GetValueOrDefault(slug, slug);
    }

    /// <summary>Strip price tag from name, then trim.</summary>
    private static string CleanName(string name) => PriceTail.Replace(name, "").TrimEnd();

}
