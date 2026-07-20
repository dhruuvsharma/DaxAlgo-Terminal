# TradingTerminal.Infrastructure / Threading — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Threading/WpfDispatcher.cs
```cs
    8: public sealed class WpfDispatcher : IUiDispatcher
   12: public WpfDispatcher()
   18: public bool CheckAccess() => _dispatcher.CheckAccess();
   20: public void Post(Action action) =>
   23: public Task InvokeAsync(Action action) =>
```
