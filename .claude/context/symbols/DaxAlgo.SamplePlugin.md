# DaxAlgo.SamplePlugin — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## samples/DaxAlgo.SamplePlugin/SamplePlugin.cs
```cs
   17: public sealed class SamplePlugin : IStrategyPlugin
   19: public string Name => "Sample Strategy Plugin";
   22: public string TargetSdkVersion => SdkInfo.Version;
   24: public void Register(IPluginRegistrar registrar)
   39: public sealed class SampleStrategy : ITradingStrategy
   41: public string Id => "sample.plugin";
   42: public string? BacktestStrategyId => "sample.plugin";
   43: public string DisplayName => "Sample Plugin Strategy";
   44: public string Description =>
   53: public sealed class SampleBacktestStrategy(Contract contract) : IBacktestStrategy
   57: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   58: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   59: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   60: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
```
