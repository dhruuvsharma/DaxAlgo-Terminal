# TradingTerminal.Heatmap — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Charts/TradingTerminal.Heatmap/AvaloniaUi/BookmapHeatmapAvaloniaWindow.axaml.cs
```cs
   10: public partial class BookmapHeatmapAvaloniaWindow : Window
   12: public BookmapHeatmapAvaloniaWindow() => InitializeComponent();
```

## src/linux/Charts/TradingTerminal.Heatmap/BookmapHeatmapViewModel.cs
```cs
   28: public sealed partial class BookmapHeatmapViewModel : SingleInstrumentHeatmapViewModelBase
   31: public const int VisibleColumns = 320;
   71: public BookmapHeatmapViewModel(
   84: public IReadOnlyList<TimeframeOption> Timeframes { get; } = new[]
  167: public IReadOnlyList<DepthSnapshot> AllColumns => _columns;
  170: public IReadOnlyList<ColumnStat> AllStats => _stats;
  173: public BookmapTrade[] RecentTrades() => _trades.ToArray();
  176: public VolumeProfileBucket[] VolumeProfileSnapshot()
  186: public ValueArea ComputeValueArea()
  222: protected override void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct)
  314: protected override void ResetBuffers()
  368: public enum BubbleSide
  376: public readonly record struct BookmapTrade(DateTime Time, double Price, long Size, BubbleSide Side, bool Large, bool Iceberg);
  380: public readonly record struct ColumnStat(long BuyVolume, long SellVolume, double Cvd, double Vwap, double AvgVolume);
  383: public readonly record struct VolumeProfileBucket(double Price, long BuyVolume, long SellVolume);
  386: public readonly record struct ValueArea(double Poc, double ValueAreaHigh, double ValueAreaLow, long TotalVolume);
  389: public sealed record TimeframeOption(string Label, double Seconds)
  391: public override string ToString() => Label;
```

## src/linux/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml.cs
```cs
   12: public partial class BookmapHeatmapWindow : MetroWindow
   17: public BookmapHeatmapWindow()
```

## src/linux/Charts/TradingTerminal.Heatmap/BookmapSurface.cs
```cs
   23: public sealed class BookmapSurface : FrameworkElement
  109: public BookmapSurface()
  116: public BookmapHeatmapViewModel? ViewModel
  129: public void OnDataUpdated()
  135: protected override void OnRenderSizeChanged(SizeChangedInfo info)
  142: protected override void OnRender(DrawingContext dc)
  626: protected override void OnMouseMove(MouseEventArgs e)
  633: protected override void OnMouseLeave(MouseEventArgs e)
  639: protected override void OnMouseWheel(MouseWheelEventArgs e)
  654: protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
```

## src/linux/Charts/TradingTerminal.Heatmap/HeatmapServiceCollectionExtensions.cs
```cs
    7: public static class HeatmapServiceCollectionExtensions
    9: public static IServiceCollection AddHeatmapSurface(this IServiceCollection services)
```

## src/linux/Charts/TradingTerminal.Heatmap/SingleInstrumentHeatmapViewModelBase.cs
```cs
   23: public abstract partial class SingleInstrumentHeatmapViewModelBase : ViewModelBase, IDisposable
   25: public const int MaxInstrumentsDisplayed = 500;
   30: protected IMarketDataRepository Repository { get; }
   31: protected IMarketDataHub Hub { get; }
   32: protected IMarketDataIngest Ingest { get; }
   33: protected IBrokerSelector Selector { get; }
   34: protected ILogger Logger { get; }
   42: protected SingleInstrumentHeatmapViewModelBase(
   66: public ObservableCollection<SignalInstrument> Instruments { get; }
   73: public event EventHandler? HeatmapUpdated;
   83: protected abstract void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct);
   86: protected abstract void ResetBuffers();
   91: protected void MarkDirty() => _dirty = true;
   94: protected void AddStreamHandle(IDisposable handle) => _streamHandles.Add(handle);
   98: protected void PumpDepth(InstrumentId id, CancellationToken ct, Action<DepthSnapshot> onUi)
  103: protected void PumpTrades(InstrumentId id, CancellationToken ct, Action<TradePrint> onUi)
  213: protected BrokerKind ResolveBroker(SignalInstrument instrument)
  223: protected static string BrokerLabel(BrokerKind broker) => broker switch
  245: public virtual void Dispose()
```
