# TradingTerminal.VolumeFootprint — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Charts/TradingTerminal.VolumeFootprint/AvaloniaUi/VolumeFootprintAvaloniaWindow.axaml.cs
```cs
    9: public partial class VolumeFootprintAvaloniaWindow : Window
   11: public VolumeFootprintAvaloniaWindow() => InitializeComponent();
```

## src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintModels.cs
```cs
    7: public enum PocSeries { Total, Buy, Sell }
   17: public enum CellDisplayMode { BidAsk, Delta, Volume }
   22: public sealed record PocFitCurve(CurveFitKind Kind, PocSeries Series, IReadOnlyList<double> Prices);
   26: public sealed record PredictedBar(double Poc, double BuyPoc, double SellPoc);
   35: public sealed class RenderBar
   40: public RenderBar(FootprintBar core)
   92: public FootprintBar Core { get; }
   96: public DateTime StartUtc      => Core.StartUtc;
   97: public long     TotalVolume   => Core.TotalVolume;
   98: public long     Delta         => Core.Delta;
   99: public long     CumulativeDelta => Core.CumulativeDelta;
  102: public double PointOfControl  => Core.PocPrice;
  106: public double BuyPointOfControl  { get; }
  109: public double SellPointOfControl { get; }
  112: public double ValueAreaHigh { get; }
  115: public double ValueAreaLow { get; }
  118: public long MaxCellVolume { get; }
  121: public int StackedBuy => Core.StackedBuy;
  124: public int StackedSell => Core.StackedSell;
  127: public IReadOnlyList<Core.MarketData.FootprintFeatureRow> Cells => Core.Rows;
  130: public FeedQuality Quality => Core.Quality;
```

## src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintServiceCollectionExtensions.cs
```cs
    7: public static class VolumeFootprintServiceCollectionExtensions
    9: public static IServiceCollection AddFootprintSurface(this IServiceCollection services)
```

## src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs
```cs
   27: public sealed partial class VolumeFootprintViewModel : ViewModelBase, IDisposable
   29: public const int MaxInstrumentsDisplayed = 500;
   92: public sealed record FootprintInterval(string Label, TimeSpan Span);
  102: public VolumeFootprintViewModel(
  138: public ObservableCollection<SignalInstrument> Instruments { get; }
  139: public ObservableCollection<FootprintInterval> Intervals { get; }
  142: public IReadOnlyList<CellDisplayMode> DisplayModes { get; } =
  146: public ObservableCollection<RenderBar> Bars { get; }
  202: public IReadOnlyList<PocFitCurve> FitCurves { get; private set; } = Array.Empty<PocFitCurve>();
  206: public IReadOnlyList<PredictedBar> Predicted { get; private set; } = Array.Empty<PredictedBar>();
  210: public double PocSlope { get; private set; }
  213: public double PocIntercept { get; private set; }
  216: public bool HasRegression { get; private set; }
  219: public double BuyPocSlope { get; private set; }
  220: public double BuyPocIntercept { get; private set; }
  221: public bool HasBuyRegression { get; private set; }
  224: public double SellPocSlope { get; private set; }
  225: public double SellPocIntercept { get; private set; }
  226: public bool HasSellRegression { get; private set; }
  229: public event EventHandler? FootprintChanged;
  709: public void Dispose()
  726: public TradePrint? Synthesize(Quote q)
  738: public void Reset() => _prev = null;
```

## src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs
```cs
   24: public partial class VolumeFootprintWindow : MetroWindow
   79: public VolumeFootprintWindow()
```
