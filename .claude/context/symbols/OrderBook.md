# TradingTerminal.OrderBook — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookModels.cs
```cs
    7: public sealed record OrderBookLevel(double Price, long Size, long Cumulative, double BarFraction);
   11: public sealed record TradeMark(double Price, long Size, AggressorSide Side);
   20: public sealed class HeatColumn
   22: public required DateTime TimeUtc { get; init; }
   25: public required IReadOnlyList<DepthLevel> Bids { get; init; }
   28: public required IReadOnlyList<DepthLevel> Asks { get; init; }
   30: public required double BestBid { get; init; }
   31: public required double BestAsk { get; init; }
   34: public required double Microprice { get; init; }
   37: public required double Imbalance { get; init; }
   40: public List<TradeMark>? Trades { get; set; }
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookPanel.xaml.cs
```cs
   25: public partial class OrderBookPanel : UserControl
   29: public static readonly DependencyProperty FeaturesProperty = DependencyProperty.Register(
   33: public OrderBookPanelFeatures Features
   87: public OrderBookPanel()
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookPanelFeatures.cs
```cs
   14: public sealed record OrderBookPanelFeatures
   18: public bool Toolbar { get; init; } = true;
   21: public bool Analytics { get; init; } = true;
   26: public bool MlForecast { get; init; } = true;
   30: public bool Heatmap { get; init; } = true;
   33: public bool Ladder { get; init; } = true;
   36: public bool OptionsRail { get; init; } = true;
   39: public bool Status { get; init; } = true;
   42: public static OrderBookPanelFeatures Full { get; } = new();
   46: public static OrderBookPanelFeatures LadderOnly { get; } = new()
   57: public static OrderBookPanelFeatures Embedded { get; } = new()
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookServiceCollectionExtensions.cs
```cs
    7: public static class OrderBookServiceCollectionExtensions
    9: public static IServiceCollection AddOrderBookSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs
```cs
   44: public sealed partial class OrderBookViewModel : ViewModelBase, IDisposable
   48: public const int MaxInstrumentsDisplayed = 500;
  141: public OrderBookViewModel(
  191: public ObservableCollection<SignalInstrument> Instruments { get; }
  192: public ObservableCollection<int> SweepSizes { get; }
  195: public ObservableCollection<int> HeatWindowOptions { get; }
  198: public ObservableCollection<string> PresetNames { get; }
  201: public IReadOnlyList<HeatColumn> HeatColumns => _heatColumns;
  207: public double ImbalanceSlope { get; private set; }
  208: public double ImbalanceIntercept { get; private set; }
  209: public bool HasImbalanceTrend { get; private set; }
  262: public OrderBookForecast? MlForecast { get; private set; }
  273: public bool MlEnabled { get; set; } = true;
  277: public IReadOnlyList<LearnerOption> Learners => Forecasters.DirectionChoices;
  327: public event EventHandler? BookChanged;
 1122: public void Dispose()
 1142: public sealed record OrderBookEmbedOptions(SignalInstrument? Instrument = null, bool MlEnabled = false);
 1146: public sealed record OrderBookPreset(
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs
```cs
   11: public partial class OrderBookWindow : MetroWindow
   13: public OrderBookWindow() => InitializeComponent();
```
