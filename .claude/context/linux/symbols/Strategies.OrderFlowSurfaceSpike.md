# TradingTerminal.Strategies.OrderFlowSurfaceSpike — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/AvaloniaUi/OrderFlowSurfaceSpikeAvaloniaWindow.axaml.cs
```cs
   12: public partial class OrderFlowSurfaceSpikeAvaloniaWindow : Window
   16: public OrderFlowSurfaceSpikeAvaloniaWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddOrderFlowSurfaceSpikeStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceCalculator.cs
```cs
   14: public sealed class OrderFlowSurfaceCalculator
   16: public int TicksPerSlice { get; }
   17: public int NumSlices { get; }
   18: public double PriceBinSize { get; }
   19: public int WindowBins { get; }
   27: public long TicksSeen { get; private set; }
   29: public OrderFlowSurfaceCalculator(
   49: public void Reset()
   60: public readonly record struct AddResult(
   67: public AddResult Add(TradePrint trade, double spikeThreshold)
  117: public double[,] GetZScoreSurface()
  151: public long LatestBin => _hasLatestBin ? _latestBin : 0;
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeStrategy.cs
```cs
    5: public sealed class OrderFlowSurfaceSpikeStrategy : ITradingStrategy
    7: public string Id => "orderflow.surface.spike";
    8: public string DisplayName => "Order Flow Surface Spike";
    9: public string Description => "3D Z-score surface over a rolling [time slice × price bin] matrix of signed order flow. Enters in the direction of a confirmed spike i...
   15: public StrategyDataRequirement DataRequirement =>
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeViewModel.cs
```cs
   15: public sealed partial class OrderFlowSurfaceSpikeViewModel : ViewModelBase, IDisposable
   17: public const int MaxInstrumentsDisplayed = 500;
   47: public OrderFlowSurfaceSpikeViewModel(
   67: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  104: public double[,]? Surface { get; private set; }
  105: public long LatestBin { get; private set; }
  107: public event EventHandler? SurfaceChanged;
  199: public async Task StartStreamAsync(CancellationToken ct)
  467: public async Task StopStreamAsync()
  480: public void Dispose()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeWindow.xaml.cs
```cs
    9: public partial class OrderFlowSurfaceSpikeWindow : MetroWindow
   22: public OrderFlowSurfaceSpikeWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/QuoteDerivedTradeSynthesizer.cs
```cs
   24: public TradePrint? Synthesize(Quote q)
   45: public void Reset() => _prev = null;
```
