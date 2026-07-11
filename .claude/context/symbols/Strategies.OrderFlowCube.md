# TradingTerminal.Strategies.OrderFlowCube — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/AvaloniaUi/OrderFlowCubeAvaloniaWindow.axaml.cs
```cs
   13: public partial class OrderFlowCubeAvaloniaWindow : Window
   17: public OrderFlowCubeAvaloniaWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/DependencyInjection.cs
```cs
    7: public static class DependencyInjection
    9: public static IServiceCollection AddOrderFlowCubeStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/Engine/OrderFlowCubeStrategyEngine.cs
```cs
   35: public sealed class OrderFlowCubeStrategy : IBacktestStrategy
   37: public int WindowTrades { get; }
   38: public int BaselineTrades { get; }
   39: public double CvdImbalanceThreshold { get; }
   40: public double AggressorBuyThreshold { get; }
   41: public double SizeRatioThreshold { get; }
   42: public int HoldTrades { get; }
   43: public long Quantity { get; }
   57: public OrderFlowCubeStrategy(
   79: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   82: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   84: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  161: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  163: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeCalculator.cs
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

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubePlugin.cs
```cs
    8: public sealed class OrderFlowCubePlugin : IStrategyPlugin
   10: public string Name => "Order-Flow Cube";
   11: public string TargetSdkVersion => SdkInfo.Version;
   12: public void Register(IPluginRegistrar registrar) => registrar.Services.AddOrderFlowCubeStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeStrategy.cs
```cs
    5: public sealed class OrderFlowCubeStrategy : ITradingStrategy
    7: public string Id => "orderflow.cube";
    8: public string DisplayName => "Order Flow Cube";
    9: public string Description => "Phase-space view of order flow: CVD imbalance (trend window) vs aggressor ratio (recent window) with size-ratio markers. Detects instit...
   16: public StrategyDataRequirement DataRequirement =>
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeViewModel.cs
```cs
   19: public sealed partial class OrderFlowCubeViewModel : ViewModelBase, IDisposable
   21: public const int MaxTrailPoints = 60;
   22: public const int MaxInstrumentsDisplayed = 500;
   56: public OrderFlowCubeViewModel(
   85: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  122: public sealed record CubePoint(double Aggressor, double Cvd, double SizeRatio, DateTime At);
  124: public ObservableCollection<CubePoint> TrailPoints { get; }
  126: public event EventHandler? TrailChanged;
  158: public ObservableCollection<string> PresetNames { get; } = new();
  322: public async Task StartStreamAsync(CancellationToken ct)
  547: public async Task StopStreamAsync()
  560: public void Dispose()
  575: public sealed record OrderFlowCubePreset(
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeWindow.xaml.cs
```cs
    9: public partial class OrderFlowCubeWindow : MetroWindow
   13: public OrderFlowCubeWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/QuoteDerivedTradeSynthesizer.cs
```cs
   17: public TradePrint? Synthesize(Quote q)
   38: public void Reset() => _prev = null;
```
