using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace PoEToolbox.Shared;

/// <summary>
/// Unified configuration service.
/// Reads/writes plugin configs to the per-user config.json in sections.
/// Uses atomic write (temp file + rename) to prevent corruption.
/// </summary>
public static class ConfigService
{
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PoEToolbox");
    public static readonly string ConfigPath = Path.Combine(DataDirectory, "config.json");
    public static readonly string BackupDirectory = Path.Combine(DataDirectory, "backups");
    public static readonly string CacheDirectory = Path.Combine(DataDirectory, "cache");
    /// <summary>生成的补丁包（fx-patch diff 产物）输出目录。</summary>
    public static readonly string PatchesDirectory = Path.Combine(DataDirectory, "patches");
    private static readonly string LegacyConfigPath = Path.Combine(AppContext.BaseDirectory, "work", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object _lock = new();
    public static string? LastReadError { get; private set; }

    /// <summary>Read the full config dictionary. Returns empty dict if file doesn't exist.</summary>
    public static Dictionary<string, JsonElement> ReadFullConfig()
    {
        lock (_lock)
        {
            EnsureMigrated();
            if (!File.Exists(ConfigPath))
                return [];

            try
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts) ?? [];
            }
            catch (Exception ex)
            {
                LastReadError = ex.Message;
                var corruptPath = ConfigPath + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                try { File.Move(ConfigPath, corruptPath, false); } catch { }
                return [];
            }
        }
    }

    /// <summary>Get a plugin's config section, deserialized to T. Returns default if not found.</summary>
    public static T? GetPluginConfig<T>(string pluginName) where T : class, new()
    {
        var cfg = ReadFullConfig();
        if (cfg.TryGetValue(pluginName, out var element))
        {
            try { return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOpts); }
            catch { }
        }
        return new T();
    }

    /// <summary>Save a plugin's config section. Atomic write.</summary>
    public static void SavePluginConfig<T>(string pluginName, T config) where T : class
    {
        lock (_lock)
        {
            var cfg = ReadFullConfig();

            var element = JsonSerializer.SerializeToElement(config, JsonOpts);
            cfg[pluginName] = element;

            Directory.CreateDirectory(DataDirectory);

            var serialized = JsonSerializer.Serialize(cfg, JsonOpts);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, serialized);
            File.Move(tmp, ConfigPath, true);
        }
    }

    /// <summary>Read a raw string value from config by key. Returns null if not found.</summary>
    public static string? GetValue(string key)
    {
        var cfg = ReadFullConfig();
        return cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
    }

    /// <summary>Write a raw string value to config.</summary>
    public static void SetValue(string key, string value)
    {
        lock (_lock)
        {
            var cfg = ReadFullConfig();
            cfg[key] = JsonSerializer.SerializeToElement(value);

            Directory.CreateDirectory(DataDirectory);
            var serialized = JsonSerializer.Serialize(cfg, JsonOpts);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, serialized);
            File.Move(tmp, ConfigPath, true);
        }
    }

    private static void EnsureMigrated()
    {
        if (File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath))
            return;

        Directory.CreateDirectory(DataDirectory);
        File.Copy(LegacyConfigPath, ConfigPath, overwrite: false);
    }
}
