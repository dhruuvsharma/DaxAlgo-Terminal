# TradingTerminal.Strategies.OrderFlowSurfaceSpike — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/AvaloniaUi/OrderFlowSurfaceSpikeAvaloniaWindow.axaml.cs
```cs
   12: public partial class OrderFlowSurfaceSpikeAvaloniaWindow : Window
   16: public OrderFlowSurfaceSpikeAvaloniaWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/DependencyInjection.cs
```cs
    7: public static class DependencyInjection
    9: public static IServiceCollection AddOrderFlowSurfaceSpikeStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/Engine/OrderFlowSurfaceSpikeStrategyEngine.cs
```cs
   28: public sealed class OrderFlowSurfaceSpikeStrategy : IBacktestStrategy
   30: public int TicksPerSlice { get; }
   31: public int NumSlices { get; }
   32: public double PriceBinSize { get; }
   33: public double SpikeThreshold { get; }
   34: public long Quantity { get; }
   35: public double StopLossPips { get; }
   36: public double TakeProfitPips { get; }
   37: public int ConfirmationTicks { get; }
   50: public OrderFlowSurfaceSpikeStrategy(
   81: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   84: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   86: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  151: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  153: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceCalculator.cs
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

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikePlugin.cs
```cs
    8: public sealed class OrderFlowSurfaceSpikePlugin : IStrategyPlugin
   10: public string Name => "Order-Flow Surface Spike";
   11: public string TargetSdkVersion => SdkInfo.Version;
   12: public void Register(IPluginRegistrar registrar) => registrar.Services.AddOrderFlowSurfaceSpikeStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeStrategy.cs
```cs
    5: public sealed class OrderFlowSurfaceSpikeStrategy : ITradingStrategy
    7: public string Id => "orderflow.surface.spike";
    8: public string DisplayName => "Order Flow Surface Spike";
    9: public string Description => "3D Z-score surface over a rolling [time slice × price bin] matrix of signed order flow. Enters in the direction of a confirmed spike i...
   15: public StrategyDataRequirement DataRequirement =>
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeViewModel.cs
```cs
   19: public sealed partial class OrderFlowSurfaceSpikeViewModel : ViewModelBase, IDisposable
   21: public const int MaxInstrumentsDisplayed = 500;
   51: public OrderFlowSurfaceSpikeViewModel(
   73: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  114: public double[,]? Surface { get; private set; }
  115: public long LatestBin { get; private set; }
  117: public event EventHandler? SurfaceChanged;
  149: public ObservableCollection<string> PresetNames { get; }
  325: public async Task StartStreamAsync(CancellationToken ct)
  593: public async Task StopStreamAsync()
  606: public void Dispose()
  621: public sealed record SurfaceSpikePreset(
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeWindow.xaml.cs
```cs
    9: public partial class OrderFlowSurfaceSpikeWindow : MetroWindow
   22: public OrderFlowSurfaceSpikeWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/QuoteDerivedTradeSynthesizer.cs
```cs
   24: public TradePrint? Synthesize(Quote q)
   45: public void Reset() => _prev = null;
```
