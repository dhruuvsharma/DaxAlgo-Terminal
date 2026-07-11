# TradingTerminal.OrderBook — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.OrderBook/AvaloniaUi/OrderBookAvaloniaWindow.axaml.cs
```cs
    8: public partial class OrderBookAvaloniaWindow : Window
   10: public OrderBookAvaloniaWindow() => InitializeComponent();
```

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

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookServiceCollectionExtensions.cs
```cs
    7: public static class OrderBookServiceCollectionExtensions
    9: public static IServiceCollection AddOrderBookSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs
```cs
   44: public sealed partial class OrderBookViewModel : ViewModelBase, IDisposable
   48: public const int MaxInstrumentsDisplayed = 500;
  137: public OrderBookViewModel(
  177: public ObservableCollection<SignalInstrument> Instruments { get; }
  178: public ObservableCollection<int> SweepSizes { get; }
  181: public ObservableCollection<int> HeatWindowOptions { get; }
  184: public ObservableCollection<string> PresetNames { get; }
  187: public IReadOnlyList<HeatColumn> HeatColumns => _heatColumns;
  193: public double ImbalanceSlope { get; private set; }
  194: public double ImbalanceIntercept { get; private set; }
  195: public bool HasImbalanceTrend { get; private set; }
  248: public OrderBookForecast? MlForecast { get; private set; }
  254: public IReadOnlyList<LearnerOption> Learners => Forecasters.DirectionChoices;
  304: public event EventHandler? BookChanged;
 1099: public void Dispose()
 1111: public sealed record OrderBookPreset(
```

## src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs
```cs
   23: public partial class OrderBookWindow : MetroWindow
   73: public OrderBookWindow()
```
