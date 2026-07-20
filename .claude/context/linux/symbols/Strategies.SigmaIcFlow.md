# TradingTerminal.Strategies.SigmaIcFlow — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.SigmaIcFlow/AvaloniaUi/SigmaIcFlowStrategyAvaloniaWindow.axaml.cs
```cs
   13: public partial class SigmaIcFlowStrategyAvaloniaWindow : Window
   15: public SigmaIcFlowStrategyAvaloniaWindow() => InitializeComponent();
```

## src/linux/Strategies/TradingTerminal.Strategies.SigmaIcFlow/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddSigmaIcFlowStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategy.cs
```cs
    5: public sealed class SigmaIcFlowStrategy : ITradingStrategy
    7: public string Id => "sigma.ic.flow";
   11: public string? BacktestStrategyId => "sigmaIcFlow";
   13: public string DisplayName => "Σ⁻¹·IC Order-Flow Optimizer (tape-primary composite)";
   14: public string Description =>
   24: public StrategyDataRequirement DataRequirement =>
```

## src/linux/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyViewModel.cs
```cs
   20: public sealed record WeightRow(string SignalName, double Weight);
   32: public sealed partial class SigmaIcFlowStrategyViewModel : LiveSignalStrategyViewModelBase
   43: public sealed record CandleIntervalOption(string Label, TimeSpan Span)
   45: public override string ToString() => Label;
   48: public ObservableCollection<CandleIntervalOption> CandleIntervals { get; } = new(new[]
  154: public ObservableCollection<WeightRow> SignalWeights { get; } = new();
  159: public ObservableCollection<ApexSignalState> SignalStates { get; } = new();
  172: public sealed record PaperTradeRow(
  177: public ObservableCollection<PaperTradeRow> PaperTrades { get; } = new();
  190: public Engine.ApexScalperStrategy? EngineStrategy => _engine;
  194: public SigmaIcFlowStrategyViewModel(
  220: protected override StrategyDataRequirement DataRequirement =>
  226: protected override IBacktestStrategy BuildStrategy(Contract contract)
  252: protected override int WarmupBarCount => Math.Max(150, MaxChartCandles);
  254: protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
  262: protected override void OnBarsUpdated()
  415: protected override string? ValidateSetup()
```

## src/linux/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyWindow.xaml.cs
```cs
   15: public partial class SigmaIcFlowStrategyWindow : StrategyWindowBase
  108: public SigmaIcFlowStrategyWindow()
  125: protected override IEnumerable<WpfPlot> ChartHosts => new[] { SignalsPlot };
  127: protected override void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
  142: protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
  770: public Grid Root { get; }
  774: public LadderRow(Brush dimBrush)
  806: public void Set(double price, long size, double barWidth, Brush brush)
  814: public void Clear()
```
