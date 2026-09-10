using PoEToolbox.Sdk;

namespace PoEToolbox.Shared;

/// <summary>
/// Adapts the unified JSON configuration store to the SDK plugin contract.
/// </summary>
public sealed class PluginConfigService<T>(string pluginName) : IConfigService<T>
    where T : class, new()
{
    private T _current = new();

    public T Current => _current;

    public T Load()
    {
        _current = ConfigService.GetPluginConfig<T>(pluginName) ?? new T();
        return _current;
    }

    public void Save(T config)
    {
        ConfigService.SavePluginConfig(pluginName, config);
        _current = config;
    }
}
