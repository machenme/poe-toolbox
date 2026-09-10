namespace PoEToolbox.Sdk;

/// <summary>
/// Lightweight pub/sub event bus for in-process plugin communication.
/// </summary>
public interface IEventBus
{
    void Publish<T>(T @event) where T : class;
    void Subscribe<T>(Action<T> handler) where T : class;
    void Unsubscribe<T>(Action<T> handler) where T : class;
}
