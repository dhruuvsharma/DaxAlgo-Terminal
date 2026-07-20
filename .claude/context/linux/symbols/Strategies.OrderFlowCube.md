# TradingTerminal.Strategies.OrderFlowCube — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/AvaloniaUi/OrderFlowCubeAvaloniaWindow.axaml.cs
```cs
   13: public partial class OrderFlowCubeAvaloniaWindow : Window
   17: public OrderFlowCubeAvaloniaWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddOrderFlowCubeStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeCalculator.cs
```cs
   19: public sealed class OrderFlowCubeCalculator
   21: public int RecentWindow { get; }
   22: public int TrendWindow { get; }
   23: public int BaselineWindow { get; }
   32: public OrderFlowCubeCalculator(
   49: public bool IsWarm =>
   57: public void Reset()
   65: public void Add(TradePrint trade)
  101: public double AggressorRatio => _recentTotal > 0 ? (double)_recentBuy / _recentTotal : 0.5;
  104: public double CvdImbalance => _trendTotal > 0 ? (double)(_trendBuy - _trendSell) / _trendTotal : 0;
  108: public double SizeRatio
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeStrategy.cs
```cs
    5: public sealed class OrderFlowCubeStrategy : ITradingStrategy
    7: public string Id => "orderflow.cube";
    8: public string DisplayName => "Order Flow Cube";
    9: public string Description => "Phase-space view of order flow: CVD imbalance (trend window) vs aggressor ratio (recent window) with size-ratio markers. Detects instit...
   16: public StrategyDataRequirement DataRequirement =>
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeViewModel.cs
```cs
   15: public sealed partial class OrderFlowCubeViewModel : ViewModelBase, IDisposable
   17: public const int MaxTrailPoints = 60;
   18: public const int MaxInstrumentsDisplayed = 500;
   52: public OrderFlowCubeViewModel(
   79: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  112: public sealed record CubePoint(double Aggressor, double Cvd, double SizeRatio, DateTime At);
  114: public ObservableCollection<CubePoint> TrailPoints { get; }
  116: public event EventHandler? TrailChanged;
  204: public async Task StartStreamAsync(CancellationToken ct)
  429: public async Task StopStreamAsync()
  442: public void Dispose()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeWindow.xaml.cs
```cs
    9: public partial class OrderFlowCubeWindow : MetroWindow
   13: public OrderFlowCubeWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowCube/QuoteDerivedTradeSynthesizer.cs
```cs
   17: public TradePrint? Synthesize(Quote q)
   38: public void Reset() => _prev = null;
```
