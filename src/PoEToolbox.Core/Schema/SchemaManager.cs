using System.Security.Cryptography;
using System.Text.Json;
using PoEToolbox.Shared;

namespace PoEToolbox.Core.Schema;

/// <summary>
/// Schema management — download, cache, and load table definitions from schema.min.json.
/// Schema is sourced from poe-tool-dev/dat-schema.
/// </summary>
public static class SchemaManager
{
    private const string EmbeddedSchemaResource = "PoEToolbox.Core.schema.min.json";
    private const string PrimaryUrl =
        "https://gh-proxy.org/https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json";
    private const string BackupUrl =
        "https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json";

    // 即时求值而不是 static readonly 快照：ConfigService 的数据根目录有测试注入缝，
    // 快照会在类型首次使用时把真实路径钉死，缝就失效了。
    private static string WorkDir => Path.Combine(ConfigService.DataDirectory, "schema");
    private static string SchemaPath => Path.Combine(WorkDir, "schema.min.json");

    private static List<TableSchema>? _cachedTables;
    private static readonly HttpClient _http = new() { Timeout = NetworkDefaults.RequestTimeout };
    private const long MaxSchemaBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Ensure schema.min.json exists and is up to date.
    /// </summary>
    public static async Task EnsureSchemaAsync()
    {
        Directory.CreateDirectory(WorkDir);

        var dest = SchemaPath;
        var localHash = FileHash(dest);
        var tmp = dest + ".tmp";

        // Try primary URL
        if (await DownloadToAsync(PrimaryUrl, tmp))
        {
            var tmpHash = FileHash(tmp);
            if (localHash != null && tmpHash == localHash)
            {
                Console.WriteLine("Schema unchanged, skipped update.");
                File.Delete(tmp);
                return;
            }
            File.Move(tmp, dest, true);
            _cachedTables = null;
            Console.WriteLine($"Schema updated: {dest}");
            return;
        }

        // Try backup URL
        Console.WriteLine("Primary URL failed, trying backup...");
        if (await DownloadToAsync(BackupUrl, tmp))
        {
            var tmpHash = FileHash(tmp);
            if (localHash != null && tmpHash == localHash)
            {
                Console.WriteLine("Schema unchanged, skipped update.");
                File.Delete(tmp);
                return;
            }
            File.Move(tmp, dest, true);
            _cachedTables = null;
            Console.WriteLine($"Schema updated (backup): {dest}");
            return;
        }

        // Both failed
        File.Delete(tmp);
        if (localHash != null)
        {
            Console.WriteLine("Download failed, using cached schema.");
            return;
        }

        if (TryRestoreEmbeddedSchema(dest))
        {
            _cachedTables = null;
            Console.WriteLine($"Download failed, using bundled schema: {dest}");
            return;
        }

        throw new InvalidOperationException(
            "Cannot download schema.min.json and no local cache available.");
    }

    /// <summary>
    /// Load all tables from schema.min.json, ensuring it exists.
    /// </summary>
    public static List<TableSchema> LoadAllTables()
    {
        if (_cachedTables != null) return _cachedTables;

        // 离线优先：本地有缓存就直接用，别为了「可能是新版本」把调用方（往往是 UI 线程）卡在网络请求上。
        // 这里曾经是 EnsureSchemaAsync().GetAwaiter().GetResult()，在 UI 线程上同步等两个 URL 各 30 秒，
        // 网络不通时表现为「点了没反应」，最坏情况下整窗假死。更新交给调用方显式调 EnsureSchemaAsync。
        var path = SchemaPath;
        if (!File.Exists(path))
        {
            if (!TryRestoreEmbeddedSchema(path))
                throw new FileNotFoundException($"Schema file not found: {path}. Ensure network connectivity.");

            _cachedTables = null;
        }

        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        var tables = new List<TableSchema>();

        foreach (var table in doc.RootElement.GetProperty("tables").EnumerateArray())
        {
            var name = table.GetProperty("name").GetString()!;
            var validFor = table.TryGetProperty("validFor", out var vf) ? vf.GetInt32() : 0;
            var columns = new List<ColumnDef>();

            foreach (var col in table.GetProperty("columns").EnumerateArray())
            {
                var colName = col.TryGetProperty("name", out var cn) ? cn.GetString() ?? $"Unknown{columns.Count}" : $"Unknown{columns.Count}";
                var colType = col.TryGetProperty("type", out var ct) ? ct.GetString() ?? "i32" : "i32";
                var isArray = col.TryGetProperty("array", out var arr) && arr.GetBoolean();
                var isInterval = col.TryGetProperty("interval", out var interval) && interval.GetBoolean();
                columns.Add(new ColumnDef(colName, colType, isArray, isInterval));
            }

            tables.Add(new TableSchema(name, validFor, columns.AsReadOnly()));
        }

        _cachedTables = tables;
        return tables;
    }

    /// <summary>
    /// Load a specific table schema by name and game version.
    /// </summary>
    /// <param name="tableName">Table name, case-insensitive.</param>
    /// <param name="validFor">Game version: 1=PoE1, 2=PoE2.</param>
    public static TableSchema LoadTable(string tableName, int validFor = 2)
    {
        var tables = LoadAllTables();

        // Exact match by name + validFor
        foreach (var t in tables)
        {
            if (string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase)
                && t.ValidFor == validFor)
                return t;
        }

        // Fallback: first match by name
        foreach (var t in tables)
        {
            if (string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
                return t;
        }

        throw new KeyNotFoundException($"Table '{tableName}' not found in schema (validFor={validFor})");
    }

    private static int _refreshStarted;

    /// <summary>
    /// Fire-and-forget schema refresh for startup: never blocks the caller, never throws.
    /// Safe to call from a UI thread — the download runs on the thread pool and the
    /// cached tables are dropped only after a newer file has actually landed on disk.
    /// </summary>
    public static void EnsureSchemaInBackground()
    {
        // 多次触发（每次开新窗口、每次切模块）只发起一次下载。
        if (Interlocked.Exchange(ref _refreshStarted, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureSchemaAsync();
            }
            catch (Exception ex)
            {
                // 拿不到新 schema 不影响使用：本地缓存或内嵌兜底已经在位。
                FileLogger.App.Warn($"后台更新 schema 失败，继续使用本地版本：{ex.Message}");
            }
        });
    }

    // ═══ Helpers ═════════════════════════════════════════════

    private static string? FileHash(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static void ValidateSchemaFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("tables", out var tables)
            || tables.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Schema response does not contain a tables array.");
    }

    private static bool TryRestoreEmbeddedSchema(string destination)
    {
        try
        {
            using var resource = typeof(SchemaManager).Assembly
                .GetManifestResourceStream(EmbeddedSchemaResource)
                ?? throw new FileNotFoundException($"Embedded resource was not found: {EmbeddedSchemaResource}");
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            File.WriteAllBytes(destination, memory.ToArray());
            ValidateSchemaFile(destination);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (Exception cleanupEx)
            {
                FileLogger.App.Warn($"无法清理内嵌 schema 临时文件：{destination}（{cleanupEx.Message}）");
            }

            FileLogger.App.Warn($"恢复内嵌 schema 失败：{ex.Message}", ex);
            return false;
        }
    }

    private static async Task<bool> DownloadToAsync(string url, string dest)
    {
        try
        {
            Console.WriteLine($"  Downloading: {url[..Math.Min(80, url.Length)]}...");
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxSchemaBytes)
                throw new InvalidDataException("Schema response exceeds the 16 MiB limit.");

            await using var source = await response.Content.ReadAsStreamAsync();
            await using (var fs = File.Create(dest))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxSchemaBytes)
                        throw new InvalidDataException("Schema response exceeds the 16 MiB limit.");
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            ValidateSchemaFile(dest);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(dest)) File.Delete(dest); }
            catch (Exception cleanupEx)
            {
                Console.WriteLine($"  Could not remove partial download: {dest} ({cleanupEx.Message})");
            }
            // GUI 里 Console 不可见，而 schema 拉不下来会让 datc64 浏览莫名其妙地失败，必须留痕。
            FileLogger.App.Warn($"Schema 下载失败（{url}）：{ex.Message}", ex);
            Console.WriteLine($"  Download failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Table schema definition.
/// </summary>
public sealed record TableSchema(
    string Name,
    int ValidFor,  // 1=PoE1, 2=PoE2
    IReadOnlyList<ColumnDef> Columns
);

/// <summary>
/// Column definition in a table schema.
/// </summary>
public sealed record ColumnDef(
    string Name,
    string Type,    // "string", "i32", "row", "array", "bool", etc.
    bool IsArray,
    bool IsInterval = false
)
{
    /// <summary>
    /// Create a copy with modified IsArray flag (used by Datc64 encoder for element sub-columns).
    /// </summary>
    public ColumnDef WithIsArray(bool isArray) => this with { IsArray = isArray };

    public ColumnDef WithIsInterval(bool isInterval) => this with { IsInterval = isInterval };
}
