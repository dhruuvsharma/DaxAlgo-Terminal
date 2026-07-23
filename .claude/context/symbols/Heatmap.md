# TradingTerminal.Heatmap — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapViewModel.cs
```cs
   33: public sealed partial class BookmapHeatmapViewModel : SingleInstrumentHeatmapViewModelBase
   36: public const int VisibleColumns = 320;
   76: public BookmapHeatmapViewModel(
   90: public ObservableCollection<string> PresetNames { get; }
   93: public IReadOnlyList<TimeframeOption> Timeframes { get; } = new[]
  284: public IReadOnlyList<DepthSnapshot> AllColumns => _columns;
  287: public IReadOnlyList<ColumnStat> AllStats => _stats;
  290: public BookmapTrade[] RecentTrades() => _trades.ToArray();
  293: public VolumeProfileBucket[] VolumeProfileSnapshot()
  303: public ValueArea ComputeValueArea()
  339: protected override void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct)
  431: protected override void ResetBuffers()
  485: public enum BubbleSide
  493: public readonly record struct BookmapTrade(DateTime Time, double Price, long Size, BubbleSide Side, bool Large, bool Iceberg);
  497: public readonly record struct ColumnStat(long BuyVolume, long SellVolume, double Cvd, double Vwap, double AvgVolume);
  500: public readonly record struct VolumeProfileBucket(double Price, long BuyVolume, long SellVolume);
  503: public readonly record struct ValueArea(double Poc, double ValueAreaHigh, double ValueAreaLow, long TotalVolume);
  506: public sealed record TimeframeOption(string Label, double Seconds)
  508: public override string ToString() => Label;
  513: public sealed record BookmapPreset(
```

## src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml.cs
```cs
   12: public partial class BookmapHeatmapWindow : MetroWindow
   17: public BookmapHeatmapWindow()
```

## src/windows/Charts/TradingTerminal.Heatmap/BookmapSurface.cs
```cs
   23: public sealed class BookmapSurface : FrameworkElement
  102: public BookmapSurface()
  109: public BookmapHeatmapViewModel? ViewModel
  122: public void OnDataUpdated()
  128: protected override void OnRenderSizeChanged(SizeChangedInfo info)
  135: protected override void OnRender(DrawingContext dc)
  645: protected override void OnMouseMove(MouseEventArgs e)
  652: protected override void OnMouseLeave(MouseEventArgs e)
  658: protected override void OnMouseWheel(MouseWheelEventArgs e)
  673: protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
```

## src/windows/Charts/TradingTerminal.Heatmap/HeatmapServiceCollectionExtensions.cs
```cs
    7: public static class HeatmapServiceCollectionExtensions
    9: public static IServiceCollection AddHeatmapSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.Heatmap/SingleInstrumentHeatmapViewModelBase.cs
```cs
   23: public abstract partial class SingleInstrumentHeatmapViewModelBase : ViewModelBase, IDisposable
   25: public const int MaxInstrumentsDisplayed = 500;
   29: protected virtual string InstrumentPersistKey => "tool.heatmap";
   34: protected IMarketDataRepository Repository { get; }
   35: protected IMarketDataHub Hub { get; }
   36: protected IMarketDataIngest Ingest { get; }
   37: protected IBrokerSelector Selector { get; }
   38: protected ILogger Logger { get; }
   46: protected SingleInstrumentHeatmapViewModelBase(
   72: public ObservableCollection<SignalInstrument> Instruments { get; }
   79: public event EventHandler? HeatmapUpdated;
   89: protected abstract void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct);
   92: protected abstract void ResetBuffers();
   97: protected void MarkDirty() => _dirty = true;
  100: protected void AddStreamHandle(IDisposable handle) => _streamHandles.Add(handle);
  104: protected void PumpDepth(InstrumentId id, CancellationToken ct, Action<DepthSnapshot> onUi)
  109: protected void PumpTrades(InstrumentId id, CancellationToken ct, Action<TradePrint> onUi)
  213: protected BrokerKind ResolveBroker(SignalInstrument instrument)
  223: protected static string BrokerLabel(BrokerKind broker) => broker switch
  245: public virtual void Dispose()
```
