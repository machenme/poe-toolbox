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
    private const string PrimaryUrl =
        "https://gh-proxy.org/https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json";
    private const string BackupUrl =
        "https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json";

    private static readonly string WorkDir = Path.Combine(ConfigService.DataDirectory, "schema");
    private static string SchemaPath => Path.Combine(WorkDir, "schema.min.json");

    private static List<TableSchema>? _cachedTables;
    private static readonly HttpClient _http = new();

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

        throw new InvalidOperationException(
            "Cannot download schema.min.json and no local cache available.");
    }

    /// <summary>
    /// Load all tables from schema.min.json, ensuring it exists.
    /// </summary>
    public static List<TableSchema> LoadAllTables()
    {
        if (_cachedTables != null) return _cachedTables;

        // Sync load for simplicity — call EnsureSchemaAsync() first in startup
        var path = SchemaPath;
        if (!File.Exists(path))
        {
            // Attempt sync download
            try
            {
                EnsureSchemaAsync().GetAwaiter().GetResult();
            }
            catch
            {
                throw new FileNotFoundException($"Schema file not found: {path}. Ensure network connectivity.");
            }
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

    // ═══ Helpers ═════════════════════════════════════════════

    private static string? FileHash(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<bool> DownloadToAsync(string url, string dest)
    {
        try
        {
            Console.WriteLine($"  Downloading: {url[..Math.Min(80, url.Length)]}...");
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(dest);
            await response.Content.CopyToAsync(fs);
            return true;
        }
        catch (Exception ex)
        {
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
