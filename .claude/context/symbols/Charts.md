# TradingTerminal.Charts — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.Charts/ChartsServiceCollectionExtensions.cs
```cs
    7: public static class ChartsServiceCollectionExtensions
    9: public static IServiceCollection AddChartsSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.Charts/ChartsViewModel.cs
```cs
   24: public sealed partial class ChartsViewModel : ViewModelBase, IDisposable
   26: public const int MaxInstrumentsDisplayed = 500;
   58: public ChartsViewModel(
   79: public ObservableCollection<ChartTimeframe> Timeframes { get; }
   80: public ObservableCollection<TradableInstrument> Instruments { get; }
   81: public ObservableCollection<string> PresetNames { get; }
   84: public IReadOnlyList<string> ChartTypes { get; } = new[] { "Candles", "Bars", "Line", "Area" };
  109: public event EventHandler<ChartSnapshot>? SnapshotReady;
  112: public event EventHandler<ChartCandle>? CandleUpdated;
  150: public Task NotifyChartReadyAsync()
  468: public void Dispose()
  479: public sealed record ChartTimeframe(string Label, BarSize BarSize, TimeSpan Lookback);
  485: public sealed record ChartsPreset(
  495: public sealed record ChartCandle(long Time, double Open, double High, double Low, double Close);
  496: public sealed record ChartVolume(long Time, double Value, string Color);
  497: public sealed record ChartLinePoint(long Time, double Value);
  498: public sealed record MacdPoint(long Time, double Macd, double Signal, double Hist);
  499: public sealed record ChartSnapshot(
```

## src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml.cs
```cs
   16: public partial class ChartsWindow : MetroWindow
   23: public ChartsWindow()
```
