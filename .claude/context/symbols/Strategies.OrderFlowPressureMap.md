# TradingTerminal.Strategies.OrderFlowPressureMap — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/AvaloniaUi/OrderFlowPressureMapAvaloniaWindow.axaml.cs
```cs
   10: public partial class OrderFlowPressureMapAvaloniaWindow : Window
   12: public OrderFlowPressureMapAvaloniaWindow() => InitializeComponent();
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddOrderFlowPressureMapStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapPlugin.cs
```cs
    8: public sealed class OrderFlowPressureMapPlugin : IStrategyPlugin
   10: public string Name => "1-Minute Order-Flow Pressure Map";
   11: public string TargetSdkVersion => SdkInfo.Version;
   12: public void Register(IPluginRegistrar registrar) => registrar.Services.AddOrderFlowPressureMapStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapStrategy.cs
```cs
   14: public sealed class OrderFlowPressureMapStrategy : ITradingStrategy
   16: public string Id => "orderflow.pressuremap";
   18: public string DisplayName => "1-Minute Order Flow Pressure Map";
   20: public string Description =>
   26: public StrategyDataRequirement DataRequirement =>
   30: public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Equity };
   32: public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapViewModel.cs
```cs
   28: public sealed partial class OrderFlowPressureMapViewModel : ViewModelBase, IDisposable
   52: public OrderFlowPressureMapViewModel(
  101: public string? PinnedSymbol => _pinnedSymbol;
  105: public IReadOnlyList<PressureRowSnapshot> Snapshot { get; private set; }
  108: public DateTime LatestColumnTime { get; private set; }
  110: public int Columns => _columns;
  112: public IReadOnlyList<PressureUniverse> Universes { get; } =
  115: public IReadOnlyList<SignalTypeFilter> SignalFilters { get; } =
  119: public event EventHandler? PressureMapChanged;
  143: public ObservableCollection<string> PresetNames { get; }
  229: public void Pin(string symbol)
  602: public void Dispose()
  614: public PressureRow(string symbol, string name, Contract contract, BrokerKind broker, InstrumentId id, int columns)
  624: public string Symbol { get; }
  625: public string Name { get; }
  626: public Contract Contract { get; }
  627: public BrokerKind Broker { get; }
  628: public InstrumentId Id { get; }
  630: public List<PressureCell> Cells { get; }
  631: public List<Bar> RecentBars { get; } = new();
  632: public Queue<long> ShortVol { get; } = new();
  633: public Dictionary<int, double>? TodBaseline { get; set; }
  635: public double LastBid { get; set; }
  636: public double LastAsk { get; set; }
  637: public long LastBidSize { get; set; }
  638: public long LastAskSize { get; set; }
  639: public double LastPrice { get; set; }
  641: public long BidDepth5 { get; set; }
  642: public long AskDepth5 { get; set; }
  643: public bool HasDepth { get; set; }
  645: public OhlcvBar? Forming { get; set; }
  647: public PressureSignal LastSignal { get; set; } = PressureSignal.Neutral;
  648: public DateTime? LastSignalTime { get; set; }
  651: public (double bid, double ask) CurrentDepth() =>
  657: public sealed record PressureMapPreset(
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapWindow.xaml.cs
```cs
   14: public partial class OrderFlowPressureMapWindow : MetroWindow
   21: public OrderFlowPressureMapWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/PressureMapCalculator.cs
```cs
   12: public static class PressureMapCalculator
   16: public static double CandlePosition(double high, double low, double close)
   21: public static double PriceImpact(double open, double close, double atr14)
   26: public static double BookImbalance(double bidDepth, double askDepth)
   34: public static double Atr(IReadOnlyList<Bar> bars, int period)
   54: public static double Intensity(double relativeVolume) => relativeVolume switch
   68: public static PressureSignal Classify(
   96: public static PressureCell Evaluate(
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/PressureMapModels.cs
```cs
   10: public sealed record PressureCell(
   29: public sealed class PressureRowSnapshot
   31: public required string Symbol { get; init; }
   32: public required string Name { get; init; }
   33: public required IReadOnlyList<PressureCell?> Cells { get; init; }
   36: public double LastPrice { get; init; }
   39: public double BookImbalance { get; init; }
   42: public double RelativeVolume { get; init; }
   44: public PressureSignal LastSignal { get; init; }
   45: public DateTime? LastSignalTime { get; init; }
   49: public bool HasActiveSignal { get; init; }
```

## src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/PressureMatrixView.cs
```cs
   21: public const double LabelWidth = 72;
   22: public const double HeaderHeight = 20;
   23: public const double RowHeight = 15;
   24: public const double CellWidth = 16;
   50: public void SetData(IReadOnlyList<PressureRowSnapshot> rows, int cols, DateTime latest, string? pinned)
   62: public (int row, int col) HitTest(Point p)
   70: protected override Size MeasureOverride(Size availableSize)
   76: protected override void OnRender(DrawingContext dc)
```
