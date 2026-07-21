# DaxAlgo.SamplePlugin — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## samples/DaxAlgo.SamplePlugin/SamplePlugin.cs
```cs
   18: public sealed class SamplePlugin : IStrategyPlugin
   20: public string Name => "Sample Strategy Plugin";
   23: public string TargetSdkVersion => SdkInfo.Version;
   25: public void Register(IPluginRegistrar registrar)
   47: public sealed class SampleStrategy : ITradingStrategy
   49: public string Id => "sample.plugin";
   50: public string? BacktestStrategyId => "sample.plugin";
   51: public string DisplayName => "Sample Plugin Strategy";
   52: public string Description =>
   61: public sealed class SampleBacktestStrategy(Contract contract) : IBacktestStrategy
   65: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   66: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   67: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   68: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   72: public sealed class SampleStrategyEngineFactory : IStrategyEngineFactory
   74: public StrategyParameterSchema Schema => StrategyParameterSchema.Empty;
   76: public StrategyDataRequirement DataRequirement =>
   79: public IBacktestStrategy Create(Contract contract) => Create(contract, Schema.CreateDefaults());
   81: public IBacktestStrategy Create(Contract contract, StrategyParameters parameters) =>
```
