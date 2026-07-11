# TradingTerminal.Core / Events — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Events/EventBus.cs
```cs
    5: public sealed class EventBus : IEventBus
   10: public IDisposable Subscribe<T>(Action<T> handler)
   21: public void Publish<T>(T evt)
   39: public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
   43: public sealed record StrategyOpenedEvent(string StrategyId, string DisplayName);
   44: public sealed record ConnectionStateChangedEvent(Domain.ConnectionState State, string? Reason = null);
```

## src/windows/Core/TradingTerminal.Core/Events/IEventBus.cs
```cs
    7: public interface IEventBus
    9:     IDisposable Subscribe<T>(Action<T> handler);
   10:     void Publish<T>(T evt);
```
