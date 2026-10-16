using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser.Models;

public partial class DataBrowserConfig
{
    public string? LastDataPath { get; set; }
    public string? LastExtractDir { get; set; }
    public double EditorFontSize { get; set; } = 12;

    /// <summary>
    /// 源生成上下文：反射序列化在 Native AOT 下被禁用。
    /// 用 <see cref="ConfigService.CreatePluginConfigJsonOptions"/> 构造实例而非 <c>Default</c>，
    /// 保住既有 config.json 的读法（大小写不敏感 + 宽松转义）。
    /// </summary>
    [JsonSerializable(typeof(DataBrowserConfig))]
    internal sealed partial class DataBrowserConfigJsonContext : JsonSerializerContext;

    internal static readonly JsonTypeInfo<DataBrowserConfig> ConfigTypeInfo =
        new DataBrowserConfigJsonContext(ConfigService.CreatePluginConfigJsonOptions()).DataBrowserConfig;
}
