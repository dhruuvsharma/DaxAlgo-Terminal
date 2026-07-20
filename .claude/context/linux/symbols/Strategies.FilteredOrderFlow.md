# TradingTerminal.Strategies.FilteredOrderFlow — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddFilteredOrderFlowStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowStrategy.cs
```cs
   10: public sealed class FilteredOrderFlowStrategy : ITradingStrategy
   12: public string Id => "filtered.orderflow.imbalance";
   13: public string DisplayName => "Filtered Order-Flow Imbalance";
   15: public string Description =>
   24: public StrategyDataRequirement DataRequirement =>
   29: public string? ResearchPaperUrl => "https://arxiv.org/abs/2507.22712";
```

## src/linux/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowViewModel.cs
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

## src/linux/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/FilteredOrderFlowWindow.xaml.cs
```cs
   14: public partial class FilteredOrderFlowWindow : StrategyWindowBase
   18: public FilteredOrderFlowWindow()
   27: protected override IEnumerable<WpfPlot> ChartHosts => new[] { ObiPlot };
   30: protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase vm) => RedrawObi();
```
