using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.PriceTagger;

/// <summary>
/// <see cref="PriceTaggerConfig"/> 的源生成上下文。
/// 刻意放在插件而不是 <c>Shared</c>：context 生成的属性是 <c>internal</c>，
/// 放到 Shared 就得给插件加 <c>InternalsVisibleTo</c>；而唯一的使用者就是本插件。
/// 用 <see cref="ConfigService.CreatePluginConfigJsonOptions"/> 构造实例而非 <c>Default</c>，
/// 保住既有 config.json 的读法（大小写不敏感 + 宽松转义）。
/// </summary>
[JsonSerializable(typeof(PriceTaggerConfig))]
internal sealed partial class PriceTaggerConfigJsonContext : JsonSerializerContext
{
    internal static readonly PriceTaggerConfigJsonContext Instance = new(ConfigService.CreatePluginConfigJsonOptions());
}

/// <summary>把 context 里的类型信息取出来，供 <see cref="PluginConfigService{T}"/> 使用。</summary>
internal static class PriceTaggerConfigJson
{
    internal static readonly JsonTypeInfo<PriceTaggerConfig> ConfigTypeInfo = PriceTaggerConfigJsonContext.Instance.PriceTaggerConfig;
}
