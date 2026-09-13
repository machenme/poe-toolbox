using System.Net.Http;
using System.Text.RegularExpressions;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text;
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
    private const string PrimaryUpdateUrl = "https://v4.gh-proxy.org/https://github.com/machenme/poe-toolbox/blob/main/version.json";
    private const string FallbackUpdateUrl = "https://raw.githubusercontent.com/machenme/poe-toolbox/main/version.json";
    private const string LastCheckedAtKey = "Update.LastCheckedAt";
    private const string CachedResultKey = "Update.CachedResult";
    private const string SkippedVersionKey = "Update.SkippedVersion";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const int MaxMetadataBytes = 256 * 1024;

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
            var json = await GetBoundedStringAsync(http, PrimaryUpdateUrl).ConfigureAwait(false);
            return ParseUpdateInfo(json);
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed to fetch primary update source, trying fallback.", ex);
        }

        var fallbackJson = await GetBoundedStringAsync(http, FallbackUpdateUrl).ConfigureAwait(false);
        return ParseUpdateInfo(fallbackJson);
    }

    private static async Task<string> GetBoundedStringAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
            throw new InvalidDataException("Update metadata exceeds the 256 KiB limit.");

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: MaxMetadataBytes);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxMetadataBytes)
                throw new InvalidDataException("Update metadata exceeds the 256 KiB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read)).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static UpdateInfo? ParseUpdateInfo(string json)
    {
        var update = JsonSerializer.Deserialize<UpdateInfo>(json, JsonOptions);
        if (update is null || ParseVersion(update.Version) is null)
            throw new InvalidDataException("Update metadata has an invalid version.");
        ValidateHttpsUrl(update.ReleaseUrl, nameof(update.ReleaseUrl));
        ValidateHttpsUrl(update.DownloadUrl, nameof(update.DownloadUrl));
        if (!string.IsNullOrWhiteSpace(update.Sha256)
            && !Regex.IsMatch(update.Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Update metadata has an invalid SHA-256 value.");
        return update;
    }

    private static void ValidateHttpsUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Update metadata field {field} must be an HTTPS URL.");
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
