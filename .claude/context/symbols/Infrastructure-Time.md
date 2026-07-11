# TradingTerminal.Infrastructure / Time — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Time/SystemClock.cs
```cs
    6: public sealed class SystemClock : IClock
    8: public DateTime UtcNow => DateTime.UtcNow;
```
