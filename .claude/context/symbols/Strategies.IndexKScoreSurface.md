# TradingTerminal.Strategies.IndexKScoreSurface — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/AvaloniaUi/IndexKScoreSurfaceAvaloniaWindow.axaml.cs
```cs
   12: public partial class IndexKScoreSurfaceAvaloniaWindow : Window
   16: public IndexKScoreSurfaceAvaloniaWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/DependencyInjection.cs
```cs
    7: public static class DependencyInjection
    9: public static IServiceCollection AddIndexKScoreSurfaceStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/Engine/IndexKScoreSurfaceStrategyEngine.cs
```cs
   21: public sealed class IndexKScoreSurfaceStrategy : IBacktestStrategy
   23: public TimeSpan BarInterval { get; }
   24: public double EntryThreshold { get; }
   25: public double ExitThreshold { get; }
   26: public long Quantity { get; }
   27: public IndexKScoreParameters Parameters { get; }
   37: public IndexKScoreSurfaceStrategy(
   60: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   62: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   95: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   97: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfacePlugin.cs
```cs
    8: public sealed class IndexKScoreSurfacePlugin : IStrategyPlugin
   10: public string Name => "Index K-Score Surface";
   11: public string TargetSdkVersion => SdkInfo.Version;
   12: public void Register(IPluginRegistrar registrar) => registrar.Services.AddIndexKScoreSurfaceStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceStrategy.cs
```cs
    6: public sealed class IndexKScoreSurfaceStrategy : ITradingStrategy
    8: public string Id => "index.kscore.surface";
    9: public string DisplayName => "Index K-Score Surface";
   10: public string Description =>
   16: public StrategyDataRequirement DataRequirement =>
   20: public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Index, AssetClass.Equity };
   22: public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceViewModel.cs
```cs
   43: public sealed partial class IndexKScoreSurfaceViewModel : ViewModelBase, IDisposable
   45: public const int KHistoryLength = 30;
   68: public IndexKScoreSurfaceViewModel(
   90: public ObservableCollection<IndexFamily> Families { get; }
   91: public ObservableCollection<BarSize> BarSizes { get; }
   92: public ObservableCollection<ComponentSnapshot> Components { get; }
  130: public IndexSnapshot? LatestSnapshot { get; private set; }
  137: public double[,]? Surface { get; private set; }
  142: public double[]? Thresholds { get; private set; }
  145: public string[]? ColumnSymbols { get; private set; }
  147: public event EventHandler? SurfaceChanged;
  170: public ObservableCollection<string> PresetNames { get; } = new();
  264: public IndexKScoreParameters BuildParameters() => new()
  780: public async Task StopStreamAsync()
  798: public void Dispose()
  825: public IndexComponent Component { get; } = component;
  826: public IndexKScoreCalculator Calculator { get; } = calc;
  827: public TimeSpan BarInterval { get; } = barInterval;
  828: public object Lock { get; } = new();
  829: public DateTime CurrentBarStart { get; set; } = DateTime.MinValue;
  830: public double Open { get; set; }
  831: public double High { get; set; }
  832: public double Low { get; set; }
  833: public double Close { get; set; }
  834: public long Volume { get; set; }
  835: public double LastK { get; set; }
  841: public double ProvisionalK { get; set; } = double.NaN;
  843: public Queue<double> KHistory { get; } = new();
  850: public sealed record KScorePreset(
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceWindow.xaml.cs
```cs
    9: public partial class IndexKScoreSurfaceWindow : MetroWindow
   24: public IndexKScoreSurfaceWindow()
```
