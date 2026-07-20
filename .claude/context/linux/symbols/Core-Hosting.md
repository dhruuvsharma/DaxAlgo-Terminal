# TradingTerminal.Core / Hosting — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Hosting/ISidecarController.cs
```cs
    9: public interface ISidecarController
   12:     bool IsRunning { get; }
   16:     Task<bool> EnsureRunningAsync(CancellationToken ct = default);
```
