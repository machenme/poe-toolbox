namespace PoEToolbox.Sdk;

/// <summary>
/// Plugin-scoped configuration service.
/// Each plugin owns a section in the unified config file.
/// </summary>
public interface IConfigService<T> where T : class, new()
{
    T Current { get; }
    T Load();
    void Save(T config);
}
