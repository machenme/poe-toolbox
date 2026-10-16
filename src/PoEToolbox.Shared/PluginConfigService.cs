using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoEToolbox.Abstractions;

namespace PoEToolbox.Shared;

/// <summary>
/// Adapts the unified JSON configuration store to the SDK plugin contract.
/// </summary>
/// <param name="pluginName">config.json 里的段落名。</param>
/// <param name="typeInfo">
/// 该插件配置类型的源生成 <see cref="JsonTypeInfo{T}"/>。由插件自己声明 context 并传入——
/// 类型 <c>T</c> 定义在插件里，<c>Shared</c> 无法替它注册。用
/// <see cref="ConfigService.CreatePluginConfigJsonOptions"/> 构造 context，保住既有读法。
/// </param>
public sealed class PluginConfigService<T>(string pluginName, JsonTypeInfo<T> typeInfo) : IConfigService<T>
    where T : class, new()
{
    private T _current = new();

    public T Current => _current;

    public T Load()
    {
        _current = ConfigService.GetPluginConfig(pluginName, typeInfo) ?? new T();
        return _current;
    }

    public void Save(T config)
    {
        ConfigService.SavePluginConfig(pluginName, config, typeInfo);
        _current = config;
    }
}
