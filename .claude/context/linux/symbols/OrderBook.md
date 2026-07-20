# TradingTerminal.OrderBook — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Charts/TradingTerminal.OrderBook/AvaloniaUi/OrderBookAvaloniaWindow.axaml.cs
```cs
    8: public partial class OrderBookAvaloniaWindow : Window
   10: public OrderBookAvaloniaWindow() => InitializeComponent();
```

## src/linux/Charts/TradingTerminal.OrderBook/OrderBookModels.cs
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

## src/linux/Charts/TradingTerminal.OrderBook/OrderBookServiceCollectionExtensions.cs
```cs
    7: public static class OrderBookServiceCollectionExtensions
    9: public static IServiceCollection AddOrderBookSurface(this IServiceCollection services)
```

## src/linux/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs
```cs
   39: public sealed partial class OrderBookViewModel : ViewModelBase, IDisposable
   43: public const int MaxInstrumentsDisplayed = 500;
   95: public OrderBookViewModel(
  126: public ObservableCollection<SignalInstrument> Instruments { get; }
  127: public ObservableCollection<int> SweepSizes { get; }
  130: public IReadOnlyList<HeatColumn> HeatColumns => _heatColumns;
  136: public double ImbalanceSlope { get; private set; }
  137: public double ImbalanceIntercept { get; private set; }
  138: public bool HasImbalanceTrend { get; private set; }
  196: public event EventHandler? BookChanged;
  631: public void Dispose()
```

## src/linux/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs
```cs
   23: public partial class OrderBookWindow : MetroWindow
   48: public OrderBookWindow()
```
