using PoEToolbox.Sdk;

namespace PoEToolbox.Shared;

/// <summary>
/// In-process synchronous event bus for communication between built-in plugins.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public void Publish<T>(T @event) where T : class
    {
        Delegate[] handlers;
        lock (_lock)
        {
            handlers = _handlers.TryGetValue(typeof(T), out var registered)
                ? [.. registered]
                : [];
        }

        foreach (var handler in handlers)
            ((Action<T>)handler)(@event);
    }

    public void Subscribe<T>(Action<T> handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var registered))
            {
                registered = [];
                _handlers[typeof(T)] = registered;
            }

            if (!registered.Contains(handler))
                registered.Add(handler);
        }
    }

    public void Unsubscribe<T>(Action<T> handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var registered)) return;
            registered.Remove(handler);
            if (registered.Count == 0)
                _handlers.Remove(typeof(T));
        }
    }
}
