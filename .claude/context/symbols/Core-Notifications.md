# TradingTerminal.Core / Notifications — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Notifications/INotificationEnricher.cs
```cs
   12: public interface INotificationEnricher
   15:     bool ShouldRun(StrategyNotification notification);
   19:     Task<StrategyNotification> EnrichAsync(StrategyNotification notification, CancellationToken ct);
```

## src/windows/Core/TradingTerminal.Core/Notifications/INotificationPublisher.cs
```cs
    7: public interface INotificationPublisher
   13:     ValueTask PublishAsync(StrategyNotification notification, CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Notifications/INotificationTransport.cs
```cs
    7: public interface INotificationTransport
   10:     string Name { get; }
   13:     bool IsEnabled { get; }
   16:     Task SendAsync(StrategyNotification notification, CancellationToken ct);
```

## src/windows/Core/TradingTerminal.Core/Notifications/ISignalGate.cs
```cs
   10: public interface ISignalGate
   14:     bool ShouldSuppress(StrategyNotification notification, out string? reason);
```

## src/windows/Core/TradingTerminal.Core/Notifications/NotificationKind.cs
```cs
    3: public enum NotificationKind
```

## src/windows/Core/TradingTerminal.Core/Notifications/StrategyNotification.cs
```cs
    7: public sealed record StrategyNotification(
   16: public static StrategyNotification Test() => new(
```
