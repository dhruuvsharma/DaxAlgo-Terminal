namespace TradingTerminal.Core.Events;

/// <summary>
/// Lightweight in-process pub/sub. Use for cross-pane events (strategy opened, connection lost, etc.).
/// Subscriptions are dispatched on the publishing thread; consumers that touch UI must marshal themselves.
/// </summary>
public interface IEventBus
{
    IDisposable Subscribe<T>(Action<T> handler);
    void Publish<T>(T evt);
}
