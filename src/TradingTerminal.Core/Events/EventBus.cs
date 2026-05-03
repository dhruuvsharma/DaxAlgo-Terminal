using System.Collections.Concurrent;

namespace TradingTerminal.Core.Events;

public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _gate = new();

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            var list = _handlers.GetOrAdd(typeof(T), _ => new List<Delegate>());
            list.Add(handler);
        }
        return new Subscription(() => Unsubscribe(typeof(T), handler));
    }

    public void Publish<T>(T evt)
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        Delegate[] snapshot;
        lock (_gate) { snapshot = list.ToArray(); }
        foreach (var d in snapshot)
            ((Action<T>)d).Invoke(evt);
    }

    private void Unsubscribe(Type t, Delegate d)
    {
        if (!_handlers.TryGetValue(t, out var list)) return;
        lock (_gate) { list.Remove(d); }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

public sealed record StrategyOpenedEvent(string StrategyId, string DisplayName);
public sealed record ConnectionStateChangedEvent(Domain.ConnectionState State, string? Reason = null);
