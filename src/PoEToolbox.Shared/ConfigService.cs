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
    private static readonly string DefaultDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PoEToolbox");

    /// <summary>
    /// 测试缝：把整个数据根目录换到别处（通常是临时目录），这样「配置文件损坏」「缺目录」这类
    /// 分支能在不碰用户真实 %LocalAppData% 的前提下复现。生产代码一律留 null。
    ///
    /// 下面六个派生路径一律做成即时求值属性，不要退回 `static readonly` 快照——快照会在类型首次
    /// 使用时就把真实路径钉死，这条缝对它就不再生效（字符串拼接，读一次算一次，开销可忽略）。
    /// 唯一的例外是 <see cref="FileLogger.App"/>：它在第一次被碰到时就按当时的根目录建好了文件句柄，
    /// 之后再改缝不影响它——要断言日志内容请订阅 <see cref="FileLogger.EntryLogged"/>，别去读日志文件。
    /// </summary>
    internal static Func<string>? DataDirectoryOverride { get; set; }

    public static string DataDirectory => DataDirectoryOverride?.Invoke() ?? DefaultDataDirectory;
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string BackupDirectory => Path.Combine(DataDirectory, "backups");
    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    /// <summary>生成的补丁包（fx-patch diff 产物）输出目录。</summary>
    public static string PatchesDirectory => Path.Combine(DataDirectory, "patches");
    /// <summary>词缀上色的上色方案目录。</summary>
    public static string AffixSchemesDirectory => Path.Combine(DataDirectory, "affix-schemes");
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
                // 之后所有配置都会当成「不存在」处理（各插件回到默认值），不留日志就查不出是谁改的。
                FileLogger.App.Warn($"config.json 解析失败，本轮按空配置继续：{ex.Message}", ex);
                var corruptPath = ConfigPath + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                try
                {
                    File.Move(ConfigPath, corruptPath, false);
                }
                catch (Exception moveEx)
                {
                    FileLogger.App.Warn($"损坏的 config.json 无法备份到 {corruptPath}：{moveEx.Message}");
                }
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
            catch (Exception ex)
            {
                // 静默回落到默认值看起来像「配置没生效」，不留痕迹就只能靠猜。
                FileLogger.App.Warn($"插件配置 {pluginName}/{typeof(T).Name} 反序列化失败，改用默认值：{ex.Message}");
            }
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
