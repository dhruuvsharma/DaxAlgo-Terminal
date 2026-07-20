# TradingTerminal.Strategies.IndexKScoreSurface — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/AvaloniaUi/IndexKScoreSurfaceAvaloniaWindow.axaml.cs
```cs
   12: public partial class IndexKScoreSurfaceAvaloniaWindow : Window
   16: public IndexKScoreSurfaceAvaloniaWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddIndexKScoreSurfaceStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceStrategy.cs
```cs
    6: public sealed class IndexKScoreSurfaceStrategy : ITradingStrategy
    8: public string Id => "index.kscore.surface";
    9: public string DisplayName => "Index K-Score Surface";
   10: public string Description =>
   16: public StrategyDataRequirement DataRequirement =>
   20: public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Index, AssetClass.Equity };
   22: public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
```

## src/linux/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceViewModel.cs
```cs
   39: public sealed partial class IndexKScoreSurfaceViewModel : ViewModelBase, IDisposable
   41: public const int KHistoryLength = 30;
   64: public IndexKScoreSurfaceViewModel(
   85: public ObservableCollection<IndexFamily> Families { get; }
   86: public ObservableCollection<BarSize> BarSizes { get; }
   87: public ObservableCollection<ComponentSnapshot> Components { get; }
  125: public IndexSnapshot? LatestSnapshot { get; private set; }
  132: public double[,]? Surface { get; private set; }
  137: public double[]? Thresholds { get; private set; }
  140: public string[]? ColumnSymbols { get; private set; }
  142: public event EventHandler? SurfaceChanged;
  144: public IndexKScoreParameters BuildParameters() => new()
  658: public async Task StopStreamAsync()
  676: public void Dispose()
  703: public IndexComponent Component { get; } = component;
  704: public IndexKScoreCalculator Calculator { get; } = calc;
  705: public TimeSpan BarInterval { get; } = barInterval;
  706: public object Lock { get; } = new();
  707: public DateTime CurrentBarStart { get; set; } = DateTime.MinValue;
  708: public double Open { get; set; }
  709: public double High { get; set; }
  710: public double Low { get; set; }
  711: public double Close { get; set; }
  712: public long Volume { get; set; }
  713: public double LastK { get; set; }
  719: public double ProvisionalK { get; set; } = double.NaN;
  721: public Queue<double> KHistory { get; } = new();
```

## src/linux/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceWindow.xaml.cs
```cs
    9: public partial class IndexKScoreSurfaceWindow : MetroWindow
   24: public IndexKScoreSurfaceWindow()
```
