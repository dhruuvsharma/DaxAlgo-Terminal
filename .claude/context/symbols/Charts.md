# TradingTerminal.Charts — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.Charts/ChartsPanel.xaml.cs
```cs
   24: public partial class ChartsPanel : UserControl
   30: public static readonly DependencyProperty FeaturesProperty = DependencyProperty.Register(
   34: public ChartsPanelFeatures Features
   45: public ChartsPanel()
```

## src/windows/Charts/TradingTerminal.Charts/ChartsPanelFeatures.cs
```cs
   15: public sealed record ChartsPanelFeatures
   20: public bool Toolbar { get; init; } = true;
   23: public bool OptionsRail { get; init; } = true;
   27: public bool Indicators { get; init; } = true;
   30: public bool Status { get; init; } = true;
   33: public static ChartsPanelFeatures Full { get; } = new();
   37: public static ChartsPanelFeatures ChartOnly { get; } = new()
   47: public static ChartsPanelFeatures Embedded { get; } = new()
```

## src/windows/Charts/TradingTerminal.Charts/ChartsServiceCollectionExtensions.cs
```cs
    7: public static class ChartsServiceCollectionExtensions
    9: public static IServiceCollection AddChartsSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.Charts/ChartsViewModel.cs
```cs
   24: public sealed partial class ChartsViewModel : ViewModelBase, IDisposable
   26: public const int MaxInstrumentsDisplayed = 500;
   63: public ChartsViewModel(
   96: public ObservableCollection<ChartTimeframe> Timeframes { get; }
   97: public ObservableCollection<TradableInstrument> Instruments { get; }
   98: public ObservableCollection<string> PresetNames { get; }
  101: public IReadOnlyList<string> ChartTypes { get; } = new[] { "Candles", "Bars", "Line", "Area" };
  126: public event EventHandler<ChartSnapshot>? SnapshotReady;
  129: public event EventHandler<ChartCandle>? CandleUpdated;
  167: public Task NotifyChartReadyAsync()
  506: public void Dispose()
  522: public sealed record ChartTimeframe(string Label, BarSize BarSize, TimeSpan Lookback);
  531: public sealed record ChartsEmbedOptions(TradableInstrument? Instrument = null, BarSize BarSize = BarSize.OneMinute);
  537: public sealed record ChartsPreset(
  547: public sealed record ChartCandle(long Time, double Open, double High, double Low, double Close);
  548: public sealed record ChartVolume(long Time, double Value, string Color);
  549: public sealed record ChartLinePoint(long Time, double Value);
  550: public sealed record MacdPoint(long Time, double Macd, double Signal, double Hist);
  551: public sealed record ChartSnapshot(
```

## src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml.cs
```cs
   11: public partial class ChartsWindow : MetroWindow
   13: public ChartsWindow() => InitializeComponent();
```
