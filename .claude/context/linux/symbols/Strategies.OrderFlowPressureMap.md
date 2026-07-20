# TradingTerminal.Strategies.OrderFlowPressureMap — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/AvaloniaUi/OrderFlowPressureMapAvaloniaWindow.axaml.cs
```cs
   10: public partial class OrderFlowPressureMapAvaloniaWindow : Window
   12: public OrderFlowPressureMapAvaloniaWindow() => InitializeComponent();
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddOrderFlowPressureMapStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapStrategy.cs
```cs
   14: public sealed class OrderFlowPressureMapStrategy : ITradingStrategy
   16: public string Id => "orderflow.pressuremap";
   18: public string DisplayName => "1-Minute Order Flow Pressure Map";
   20: public string Description =>
   26: public StrategyDataRequirement DataRequirement =>
   30: public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Equity };
   32: public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapViewModel.cs
```cs
   23: public sealed partial class OrderFlowPressureMapViewModel : ViewModelBase, IDisposable
   47: public OrderFlowPressureMapViewModel(
   95: public string? PinnedSymbol => _pinnedSymbol;
   99: public IReadOnlyList<PressureRowSnapshot> Snapshot { get; private set; }
  102: public DateTime LatestColumnTime { get; private set; }
  104: public int Columns => _columns;
  106: public IReadOnlyList<PressureUniverse> Universes { get; } =
  109: public IReadOnlyList<SignalTypeFilter> SignalFilters { get; } =
  113: public event EventHandler? PressureMapChanged;
  121: public void Pin(string symbol)
  493: public void Dispose()
  505: public PressureRow(string symbol, string name, Contract contract, BrokerKind broker, InstrumentId id, int columns)
  515: public string Symbol { get; }
  516: public string Name { get; }
  517: public Contract Contract { get; }
  518: public BrokerKind Broker { get; }
  519: public InstrumentId Id { get; }
  521: public List<PressureCell> Cells { get; }
  522: public List<Bar> RecentBars { get; } = new();
  523: public Queue<long> ShortVol { get; } = new();
  524: public Dictionary<int, double>? TodBaseline { get; set; }
  526: public double LastBid { get; set; }
  527: public double LastAsk { get; set; }
  528: public long LastBidSize { get; set; }
  529: public long LastAskSize { get; set; }
  530: public double LastPrice { get; set; }
  532: public long BidDepth5 { get; set; }
  533: public long AskDepth5 { get; set; }
  534: public bool HasDepth { get; set; }
  536: public OhlcvBar? Forming { get; set; }
  538: public PressureSignal LastSignal { get; set; } = PressureSignal.Neutral;
  539: public DateTime? LastSignalTime { get; set; }
  542: public (double bid, double ask) CurrentDepth() =>
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapWindow.xaml.cs
```cs
   14: public partial class OrderFlowPressureMapWindow : MetroWindow
   21: public OrderFlowPressureMapWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/PressureMapCalculator.cs
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

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/PressureMapModels.cs
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

## src/linux/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/PressureMatrixView.cs
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
