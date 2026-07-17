# TradingTerminal.Infrastructure / Threading — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Threading/WpfDispatcher.cs
```cs
    8: public sealed class WpfDispatcher : IUiDispatcher
   12: public WpfDispatcher()
   18: public bool CheckAccess() => _dispatcher.CheckAccess();
   20: public void Post(Action action) =>
   23: public Task InvokeAsync(Action action) =>
```
