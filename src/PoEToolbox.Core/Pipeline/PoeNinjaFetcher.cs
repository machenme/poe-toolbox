using System.Text.Json;
using System.Net;
using System.Net.Http;

namespace PoEToolbox.Core.Pipeline;

/// <summary>
/// Fetches economy data from poe.ninja API for both PoE1 and PoE2.
/// Respects rate limits with configurable delays between requests.
/// </summary>
public static class PoeNinjaFetcher
{
    public enum PoeGame { Poe1, Poe2 }

    public sealed record League(string Id, string Name);

    public enum FetchStatus { Succeeded, Partial, Failed, Cancelled }

    public sealed record CategoryFetchResult(
        string Category,
        FetchStatus Status,
        string? OutputPath,
        string? Error,
        bool UsedExistingCache,
        DateTimeOffset ObservedAtUtc);

    public sealed record FetchResult(
        PoeGame Game,
        string League,
        IReadOnlyList<CategoryFetchResult> Items,
        FetchStatus Status)
    {
        public bool IsSuccess => Status == FetchStatus.Succeeded;
    }

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "PoEToolbox/0.1.2 (contact: local-tool)" } }
    };

    // ═══ API Endpoints ═════════════════════════════════════════

    private const string Poe1BaseUrl = "https://poe.ninja/poe1/api/economy/exchange/current/overview";
    private const string Poe1LeaguesUrl = "https://poe.ninja/poe1/api/economy/leagues";
    private const string Poe2BaseUrl = "https://poe.ninja/poe2/api/economy/exchange/current/overview";
    private const string Poe2LeaguesUrl = "https://poe.ninja/poe2/api/economy/leagues";

    // ═══ Exchange Types ════════════════════════════════════════

    /// <summary>PoE1 exchange/currency overview categories</summary>
    public static readonly string[] Poe1ExchangeTypes =
    [
        "Currency", "Fragment", "Runegraft", "AllflameEmber",
        "Tattoo", "Omen", "Ducat", "EnshroudingCrystal",
        "DivinationCard", "Artifact", "Oil", "DeliriumOrb",
        "Scarab", "Astrolabe", "Fossil", "Resonator", "Essence",
    ];

    /// <summary>PoE2 exchange overview categories</summary>
    public static readonly string[] Poe2ExchangeTypes =
    [
        "Currency", "Fragments", "Abyss", "UncutGems", "LineageSupportGems",
        "Essences", "SoulCores", "Idols", "Runes", "Ritual",
        "Expedition", "Delirium", "Breach", "Verisium",
    ];

    // ═══ Public API ════════════════════════════════════════════

    /// <summary>
    /// Auto-detect the current challenge league for a given game.
    /// Returns the first (current) league id from the leagues endpoint.
    /// </summary>
    public static async Task<string?> DetectLeagueAsync(string leaguesUrl, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(leaguesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object
                ? first.GetProperty("id").GetString()
                : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DetectLeague failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gets all current leagues for the specified game.</summary>
    public static async Task<IReadOnlyList<League>> GetLeaguesAsync(
        PoeGame game, CancellationToken ct = default)
    {
        var leaguesUrl = game == PoeGame.Poe1 ? Poe1LeaguesUrl : Poe2LeaguesUrl;
        try
        {
            var json = await _http.GetStringAsync(leaguesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out _))
                .Select(item => new League(
                    item.GetProperty("id").GetString() ?? string.Empty,
                    item.TryGetProperty("name", out var name)
                        ? name.GetString() ?? string.Empty
                        : string.Empty))
                .Where(league => league.Id.Length > 0)
                .ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetLeagues failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Identifies which game's economy has price data for a custom league.
    /// Returns null when both APIs have data or neither API does.
    /// </summary>
    public static async Task<PoeGame?> DetectGameForLeagueAsync(
        string league, CancellationToken ct = default)
    {
        var poe1HasData = await HasExchangeDataAsync(Poe1BaseUrl, league, ct);
        var poe2HasData = await HasExchangeDataAsync(Poe2BaseUrl, league, ct);

        return poe1HasData == poe2HasData
            ? null
            : poe1HasData ? PoeGame.Poe1 : PoeGame.Poe2;
    }

    /// <summary>
    /// Fetch all poe.ninja exchange data for PoE1.
    /// Each category is saved as a JSON file, with a 3-5s delay between requests.
    /// </summary>
    public static async Task<FetchResult> FetchPoe1Async(
        string outputDir,
        string? league = null,
        string[]? types = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        league ??= await DetectLeagueAsync(Poe1LeaguesUrl, ct)
            ?? throw new HttpRequestException("Unable to detect the current PoE1 league.");
        types ??= Poe1ExchangeTypes;

        progress?.Report($"PoE1 League: {league}");
        progress?.Report($"Categories: {types.Length}");

        return await FetchAndSaveAsync(PoeGame.Poe1, Poe1BaseUrl, league, types, outputDir, progress, ct, null);
    }

    /// <summary>
    /// Fetch all poe.ninja exchange data for PoE2.
    /// </summary>
    public static async Task<FetchResult> FetchPoe2Async(
        string outputDir,
        string? league = null,
        string[]? types = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        league ??= await DetectLeagueAsync(Poe2LeaguesUrl, ct)
            ?? throw new HttpRequestException("Unable to detect the current PoE2 league.");
        types ??= Poe2ExchangeTypes;

        progress?.Report($"PoE2 League: {league}");
        progress?.Report($"Categories: {types.Length}");

        return await FetchAndSaveAsync(PoeGame.Poe2, Poe2BaseUrl, league, types, outputDir, progress, ct, null);
    }

    /// <summary>
    /// Fetch only the specified categories. Used by the GUI when applying price tags.
    /// </summary>
    public static async Task<FetchResult> FetchSelectedAsync(
        string outputDir,
        string league,
        string[] types,
        IProgress<string>? progress = null,
        PoeGame game = PoeGame.Poe1,
        CancellationToken ct = default,
        HttpMessageHandler? handler = null)
    {
        var baseUrl = game == PoeGame.Poe1 ? Poe1BaseUrl : Poe2BaseUrl;
        return await FetchAndSaveAsync(game, baseUrl, league, types, outputDir, progress, ct, handler);
    }

    // ═══ Core ══════════════════════════════════════════════════

    private static async Task<FetchResult> FetchAndSaveAsync(
        PoeGame game,
        string baseUrl,
        string league,
        string[] types,
        string outputDir,
        IProgress<string>? progress,
        CancellationToken ct,
        HttpMessageHandler? handler)
    {
        Directory.CreateDirectory(outputDir);
        using var http = handler is null ? null : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var client = http ?? _http;
        var items = new List<CategoryFetchResult>(types.Length);

        for (var i = 0; i < types.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var type = types[i];
            var url = $"{baseUrl}?league={Uri.EscapeDataString(league)}&type={type}";
            progress?.Report($"[{i + 1}/{types.Length}] Fetching {type}...");

            try
            {
                var response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);

                // Pretty-print
                using var doc = JsonDocument.Parse(json);
                var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

                var outPath = Path.Combine(outputDir, $"{type}.json");
                var tempPath = outPath + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(tempPath, pretty, ct);
                    File.Move(tempPath, outPath, true);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }

                var lineCount = doc.RootElement.TryGetProperty("lines", out var lines)
                    ? lines.GetArrayLength() : 0;
                progress?.Report($"  -> {outPath} ({lineCount} entries)");
                items.Add(new CategoryFetchResult(type, FetchStatus.Succeeded, outPath, null, false, DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress?.Report($"  Failed: {ex.Message}");
                var outPath = Path.Combine(outputDir, $"{type}.json");
                items.Add(new CategoryFetchResult(type, FetchStatus.Failed, null, ex.Message,
                    File.Exists(outPath), DateTimeOffset.UtcNow));
            }

            // 3-5 second delay between categories (respect the API)
            if (i < types.Length - 1)
            {
                var delay = Random.Shared.Next(2000, 3001);
                progress?.Report($"  Waiting {delay / 1000.0:F1}s...");
                await Task.Delay(delay, ct);
            }
        }

        var status = items.Count == 0 || items.All(item => item.Status == FetchStatus.Failed)
            ? FetchStatus.Failed
            : items.Any(item => item.Status == FetchStatus.Failed)
                ? FetchStatus.Partial
                : FetchStatus.Succeeded;
        progress?.Report(status == FetchStatus.Succeeded ? "Done." : $"Finished with status: {status}.");
        return new FetchResult(game, league, items, status);
    }

    private static async Task<bool> HasExchangeDataAsync(
        string baseUrl, string league, CancellationToken ct)
    {
        try
        {
            var url = $"{baseUrl}?league={Uri.EscapeDataString(league)}&type=Currency";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("lines", out var lines)
                && lines.ValueKind == JsonValueKind.Array
                && lines.GetArrayLength() > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"League probe failed: {ex.Message}");
            return false;
        }
    }
}
