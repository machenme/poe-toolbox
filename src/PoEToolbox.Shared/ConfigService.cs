using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Encodings.Web;

namespace PoEToolbox.Shared;

/// <summary>
/// Unified configuration service.
/// Reads/writes plugin configs to the per-user config.json in sections.
/// Uses atomic write (temp file + rename) to prevent corruption.
/// </summary>
public static partial class ConfigService
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

    /// <summary>
    /// 插件配置的规范 JSON 选项（<strong>每次调用返回新实例</strong>）。
    ///
    /// <para>源生成上下文必须用它构造，⛔ 不能用 <c>MyContext.Default</c>：<c>Encoder</c> 写不进
    /// <c>[JsonSourceGenerationOptions]</c> 特性，用 <c>Default</c> 会丢掉宽松转义（中文被写成
    /// <c>\uXXXX</c>）；<c>PropertyNameCaseInsensitive</c> 丢了则既有 config.json 读不出来。</para>
    ///
    /// <para>为什么返回新实例而不是共享单例：一个 <see cref="JsonSerializerOptions"/> 绑定到源生成
    /// context 之后就不能再给反射式序列化用（<see cref="SetJsonValue"/> 那条路仍要反射），
    /// 所以本类的 context、各插件的 context、以及反射路径各持一份。</para>
    /// </summary>
    public static JsonSerializerOptions CreatePluginConfigJsonOptions() => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 源生成上下文：反射序列化在 Native AOT 下被禁用（运行时抛 InvalidOperationException）。
    /// 覆盖 config.json 自身读写用到的两个具体类型；插件配置段落走调用方传进来的
    /// <see cref="JsonTypeInfo{T}"/>（见 <see cref="GetPluginConfig{T}(string, JsonTypeInfo{T})"/>）。
    /// </summary>
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(string))]
    internal sealed partial class ConfigServiceJsonContext : JsonSerializerContext;

    /// <summary>给上面那个 context 用的选项实例（与 <see cref="JsonOpts"/> 同设置，但刻意不共用实例）。</summary>
    private static readonly ConfigServiceJsonContext SourceGenJson = new(CreatePluginConfigJsonOptions());

    /// <summary>反射路径（<see cref="SetJsonValue"/>）用的选项。不能与源生成 context 共用同一个实例。</summary>
    private static readonly JsonSerializerOptions JsonOpts = CreatePluginConfigJsonOptions();

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
                return JsonSerializer.Deserialize(json, SourceGenJson.DictionaryStringJsonElement) ?? [];
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

    /// <summary>
    /// Get a plugin's config section, deserialized to T. Returns default if not found.
    /// <paramref name="typeInfo"/> 由调用方（插件）自己声明并传入：类型 <c>T</c> 定义在插件里，
    /// <c>Shared</c> 不能引用插件，所以这里没法替它们注册。用
    /// <see cref="CreatePluginConfigJsonOptions"/> 构造 context，保住既有的读法（含大小写不敏感）。
    /// </summary>
    public static T? GetPluginConfig<T>(string pluginName, JsonTypeInfo<T> typeInfo) where T : class, new()
    {
        var cfg = ReadFullConfig();
        if (cfg.TryGetValue(pluginName, out var element))
        {
            try { return JsonSerializer.Deserialize(element.GetRawText(), typeInfo); }
            catch (Exception ex)
            {
                // 静默回落到默认值看起来像「配置没生效」，不留痕迹就只能靠猜。
                FileLogger.App.Warn($"插件配置 {pluginName}/{typeof(T).Name} 反序列化失败，改用默认值：{ex.Message}");
            }
        }
        return new T();
    }

    /// <summary>
    /// Save a plugin's config section. Atomic write.
    /// <paramref name="typeInfo"/> 的来由见 <see cref="GetPluginConfig{T}(string, JsonTypeInfo{T})"/>。
    /// </summary>
    public static void SavePluginConfig<T>(string pluginName, T config, JsonTypeInfo<T> typeInfo) where T : class
    {
        lock (_lock)
        {
            var cfg = ReadFullConfig();

            var element = JsonSerializer.SerializeToElement(config, typeInfo);
            cfg[pluginName] = element;
            WriteFullConfig(cfg);
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
            cfg[key] = JsonSerializer.SerializeToElement(value, SourceGenJson.String);
            WriteFullConfig(cfg);
        }
    }

    /// <summary>
    /// Write a structured JSON value to config. Atomic write.
    /// </summary>
    /// <remarks>
    /// ⛔ <strong>这条 API 不能上 Native AOT。</strong> 调用方（<c>App/UpdateChecker</c>）传的是
    /// <strong>匿名对象</strong>（<c>new { }</c>），匿名类型无法源生成，因此保留反射式实现并如实标注。
    /// 标注后警告会转移到调用方；<c>App</c> 与各插件都没开 trim/AOT 分析器，所以不产生噪音。
    /// AOT 路径上请改用带 <see cref="JsonTypeInfo{T}"/> 的 <see cref="SavePluginConfig{T}"/>。
    /// </remarks>
    [RequiresUnreferencedCode("SetJsonValue 走反射式序列化；调用方传匿名对象，无法源生成。AOT 下请改用带 JsonTypeInfo 的重载。")]
    [RequiresDynamicCode("SetJsonValue 走反射式序列化，AOT 下需要运行时代码生成。")]
    public static void SetJsonValue(string key, object value)
    {
        lock (_lock)
        {
            var cfg = ReadFullConfig();
            cfg[key] = JsonSerializer.SerializeToElement(value, JsonOpts);
            WriteFullConfig(cfg);
        }
    }

    private static void WriteFullConfig(Dictionary<string, JsonElement> config)
    {
        Directory.CreateDirectory(DataDirectory);
        var serialized = JsonSerializer.Serialize(config, SourceGenJson.DictionaryStringJsonElement);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, serialized);
        File.Move(tmp, ConfigPath, true);
    }

    private static void EnsureMigrated()
    {
        if (File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath))
            return;

        Directory.CreateDirectory(DataDirectory);
        File.Copy(LegacyConfigPath, ConfigPath, overwrite: false);
    }
}
