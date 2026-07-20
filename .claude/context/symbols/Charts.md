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
   62: public ChartsViewModel(
   95: public ObservableCollection<ChartTimeframe> Timeframes { get; }
   96: public ObservableCollection<TradableInstrument> Instruments { get; }
   97: public ObservableCollection<string> PresetNames { get; }
  100: public IReadOnlyList<string> ChartTypes { get; } = new[] { "Candles", "Bars", "Line", "Area" };
  125: public event EventHandler<ChartSnapshot>? SnapshotReady;
  128: public event EventHandler<ChartCandle>? CandleUpdated;
  166: public Task NotifyChartReadyAsync()
  484: public void Dispose()
  497: public sealed record ChartTimeframe(string Label, BarSize BarSize, TimeSpan Lookback);
  506: public sealed record ChartsEmbedOptions(TradableInstrument? Instrument = null, BarSize BarSize = BarSize.OneMinute);
  512: public sealed record ChartsPreset(
  522: public sealed record ChartCandle(long Time, double Open, double High, double Low, double Close);
  523: public sealed record ChartVolume(long Time, double Value, string Color);
  524: public sealed record ChartLinePoint(long Time, double Value);
  525: public sealed record MacdPoint(long Time, double Macd, double Signal, double Hist);
  526: public sealed record ChartSnapshot(
```

## src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml.cs
```cs
   11: public partial class ChartsWindow : MetroWindow
   13: public ChartsWindow() => InitializeComponent();
   15: protected override void OnClosed(EventArgs e)
```
