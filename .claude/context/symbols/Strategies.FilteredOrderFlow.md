# TradingTerminal.Strategies.FilteredOrderFlow — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/DependencyInjection.cs
```cs
    7: public static class DependencyInjection
    9: public static IServiceCollection AddFilteredOrderFlowStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/Engine/FilteredOrderFlowStrategyEngine.cs
```cs
   34: public sealed class FilteredOrderFlowStrategy : IBacktestStrategy
   37: public double WindowSeconds { get; }
   41: public long MinTradeSize { get; }
   44: public int StrongRegime { get; }
   48: public double HoldSeconds { get; }
   50: public long Quantity { get; }
   67: public FilteredOrderFlowStrategy(
   85: public double FilteredObi { get; private set; }
   87: public double UnfilteredObi { get; private set; }
   89: public int FilteredRegime { get; private set; }
   91: public long FilteredTradesInWindow => _buyFilt + _sellFilt;
   93: public long UnfilteredTradesInWindow => _buyAll + _sellAll;
   95: public long Position => _position;
   99: public event Action? Updated;
  101: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
  103: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  110: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  188: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  190: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowPlugin.cs
```cs
    8: public sealed class FilteredOrderFlowPlugin : IStrategyPlugin
   10: public string Name => "Filtered Order-Flow Imbalance";
   11: public string TargetSdkVersion => SdkInfo.Version;
   12: public void Register(IPluginRegistrar registrar) => registrar.Services.AddFilteredOrderFlowStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowStrategy.cs
```cs
   10: public sealed class FilteredOrderFlowStrategy : ITradingStrategy
   12: public string Id => "filtered.orderflow.imbalance";
   13: public string DisplayName => "Filtered Order-Flow Imbalance";
   15: public string Description =>
   24: public StrategyDataRequirement DataRequirement =>
   29: public string? ResearchPaperUrl => "https://arxiv.org/abs/2507.22712";
```

## src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowViewModel.cs
```cs
   22: public sealed partial class FilteredOrderFlowViewModel : LiveSignalStrategyViewModelBase
   25: public const int MaxObiSamples = 2_000;
   48: public IReadOnlyList<ObiSample> ObiHistory => _obiHistory;
   50: public FilteredOrderFlowViewModel(
   63: protected override StrategyDataRequirement DataRequirement =>
   66: protected override IBacktestStrategy BuildStrategy(Contract contract)
  103: protected override string? ValidateSetup()
  115: public sealed record ObiSample(DateTime TimeUtc, double Filtered, double Unfiltered, int Regime);
```

## src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowWindow.xaml.cs
```cs
   14: public partial class FilteredOrderFlowWindow : StrategyWindowBase
   18: public FilteredOrderFlowWindow()
   27: protected override IEnumerable<WpfPlot> ChartHosts => new[] { ObiPlot };
   30: protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase vm) => RedrawObi();
```
