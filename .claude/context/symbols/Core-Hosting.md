# TradingTerminal.Core / Hosting — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Hosting/ISidecarController.cs
```cs
    9: public interface ISidecarController
   12:     bool IsRunning { get; }
   16:     Task<bool> EnsureRunningAsync(CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Hosting/NullSidecarController.cs
```cs
    9: public sealed class NullSidecarController : ISidecarController
   11: public bool IsRunning => false;
   14: public Task<bool> EnsureRunningAsync(CancellationToken ct = default) => Task.FromResult(false);
```
