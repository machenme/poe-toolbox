using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using PoEToolbox.Shared;

namespace PoEToolbox.App;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    Version? CurrentVersion,
    Version? LatestVersion,
    string? ReleaseUrl,
    string? DownloadUrl,
    string? Notes);

public static class UpdateChecker
{
    private const string PrimaryUpdateUrl = "https://gitee.com/osmc/poe-toolbox/raw/main/version.json";
    private const string FallbackUpdateUrl = "https://raw.githubusercontent.com/machenme/poe-toolbox/main/version.json";
    private const string LastCheckedAtKey = "Update.LastCheckedAt";
    private const string CachedResultKey = "Update.CachedResult";
    private const string SkippedVersionKey = "Update.SkippedVersion";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static UpdateCheckResult GetCachedResult()
    {
        var raw = ConfigService.GetValue(CachedResultKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new UpdateCheckResult(false, GetCurrentVersion(), null, null, null, null);

        try
        {
            var cached = JsonSerializer.Deserialize<CachedUpdateState>(raw, JsonOptions);
            if (cached is null)
                return new UpdateCheckResult(false, GetCurrentVersion(), null, null, null, null);

            var currentVersion = GetCurrentVersion();
            var latestVersion = ParseVersion(cached.LatestVersion);
            return new UpdateCheckResult(
                HasUpdate(currentVersion, latestVersion),
                currentVersion,
                latestVersion,
                cached.ReleaseUrl,
                cached.DownloadUrl,
                cached.Notes);
        }
        catch
        {
            return new UpdateCheckResult(false, GetCurrentVersion(), null, null, null, null);
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        if (!ShouldCheck())
            return GetCachedResult();

        ConfigService.SetValue(LastCheckedAtKey, DateTimeOffset.UtcNow.ToString("O"));

        try
        {
            var update = await FetchUpdateInfoAsync().ConfigureAwait(false);
            var currentVersion = GetCurrentVersion();
            var latestVersion = ParseVersion(update?.Version);
            var hasUpdate = HasUpdate(currentVersion, latestVersion);

            var result = new UpdateCheckResult(
                hasUpdate,
                currentVersion,
                latestVersion,
                update?.ReleaseUrl,
                update?.DownloadUrl,
                update?.Notes);

            SaveCachedResult(result);
            return result;
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed to check for updates.", ex);
            return GetCachedResult();
        }
    }

    private static async Task<UpdateInfo?> FetchUpdateInfoAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            var json = await http.GetStringAsync(PrimaryUpdateUrl).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateInfo>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed to fetch primary update source, trying fallback.", ex);
        }

        var fallbackJson = await http.GetStringAsync(FallbackUpdateUrl).ConfigureAwait(false);
        return JsonSerializer.Deserialize<UpdateInfo>(fallbackJson, JsonOptions);
    }

    public static void ClearCachedResult()
    {
        ConfigService.SetValue(CachedResultKey, string.Empty);
    }

    public static void SkipVersion(Version version)
    {
        ConfigService.SetValue(SkippedVersionKey, version.ToString());
    }

    private static bool ShouldCheck()
    {
        var raw = ConfigService.GetValue(LastCheckedAtKey);
        if (!DateTimeOffset.TryParse(raw, out var lastCheckedAt))
            return true;

        return DateTimeOffset.UtcNow - lastCheckedAt.ToUniversalTime() >= CheckInterval;
    }

    private static bool HasUpdate(Version? currentVersion, Version? latestVersion)
    {
        if (currentVersion is null || latestVersion is null || latestVersion <= currentVersion)
            return false;

        var skippedVersion = ParseVersion(ConfigService.GetValue(SkippedVersionKey));
        return skippedVersion is null || latestVersion > skippedVersion;
    }

    private static void SaveCachedResult(UpdateCheckResult result)
    {
        var payload = new CachedUpdateState(
            result.HasUpdate,
            result.CurrentVersion?.ToString(),
            result.LatestVersion?.ToString(),
            result.ReleaseUrl,
            result.DownloadUrl,
            result.Notes);
        ConfigService.SetValue(CachedResultKey, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static Version? GetCurrentVersion() => Assembly.GetExecutingAssembly().GetName().Version;

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Version.TryParse(value.Trim().TrimStart('v', 'V'), out var version)
            ? version
            : null;
    }

    private sealed record UpdateInfo(
        string Version,
        string? ReleaseUrl,
        string? DownloadUrl,
        string? Sha256,
        string? Notes);

    private sealed record CachedUpdateState(
        bool HasUpdate,
        string? CurrentVersion,
        string? LatestVersion,
        string? ReleaseUrl,
        string? DownloadUrl,
        string? Notes);
}
