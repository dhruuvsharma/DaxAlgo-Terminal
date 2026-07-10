# TradingTerminal.VolumeFootprint — public API surface

Generated 2026-07-10. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.VolumeFootprint/AvaloniaUi/VolumeFootprintAvaloniaWindow.axaml.cs
```cs
    9: public partial class VolumeFootprintAvaloniaWindow : Window
   11: public VolumeFootprintAvaloniaWindow() => InitializeComponent();
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintModels.cs
```cs
    7: public enum PocSeries { Total, Buy, Sell }
   17: public enum CellDisplayMode { BidAsk, Delta, Volume }
   22: public sealed record PocFitCurve(CurveFitKind Kind, PocSeries Series, IReadOnlyList<double> Prices);
   26: public sealed record PredictedBar(double Poc, double BuyPoc, double SellPoc);
   31: public sealed record MlPredictedBar(
   41: public sealed class RenderBar
   46: public RenderBar(FootprintBar core)
   98: public FootprintBar Core { get; }
  102: public DateTime StartUtc      => Core.StartUtc;
  103: public long     TotalVolume   => Core.TotalVolume;
  104: public long     Delta         => Core.Delta;
  105: public long     CumulativeDelta => Core.CumulativeDelta;
  108: public double PointOfControl  => Core.PocPrice;
  112: public double BuyPointOfControl  { get; }
  115: public double SellPointOfControl { get; }
  118: public double ValueAreaHigh { get; }
  121: public double ValueAreaLow { get; }
  124: public long MaxCellVolume { get; }
  127: public int StackedBuy => Core.StackedBuy;
  130: public int StackedSell => Core.StackedSell;
  133: public IReadOnlyList<Core.MarketData.FootprintFeatureRow> Cells => Core.Rows;
  136: public FeedQuality Quality => Core.Quality;
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintServiceCollectionExtensions.cs
```cs
    7: public static class VolumeFootprintServiceCollectionExtensions
    9: public static IServiceCollection AddFootprintSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs
```cs
   31: public sealed partial class VolumeFootprintViewModel : ViewModelBase, IDisposable
   33: public const int MaxInstrumentsDisplayed = 500;
  113: public sealed record FootprintInterval(string Label, TimeSpan Span);
  123: public VolumeFootprintViewModel(
  166: public ObservableCollection<SignalInstrument> Instruments { get; }
  167: public ObservableCollection<FootprintInterval> Intervals { get; }
  170: public IReadOnlyList<CellDisplayMode> DisplayModes { get; } =
  174: public ObservableCollection<RenderBar> Bars { get; }
  177: public ObservableCollection<string> PresetNames { get; }
  244: public IReadOnlyList<LearnerOption> Learners => Forecasters.DirectionChoices;
  261: public IReadOnlyList<PocFitCurve> FitCurves { get; private set; } = Array.Empty<PocFitCurve>();
  265: public IReadOnlyList<PredictedBar> Predicted { get; private set; } = Array.Empty<PredictedBar>();
  270: public IReadOnlyList<MlPredictedBar> MlPredicted { get; private set; } = Array.Empty<MlPredictedBar>();
  274: public double PocSlope { get; private set; }
  277: public double PocIntercept { get; private set; }
  280: public bool HasRegression { get; private set; }
  283: public double BuyPocSlope { get; private set; }
  284: public double BuyPocIntercept { get; private set; }
  285: public bool HasBuyRegression { get; private set; }
  288: public double SellPocSlope { get; private set; }
  289: public double SellPocIntercept { get; private set; }
  290: public bool HasSellRegression { get; private set; }
  293: public event EventHandler? FootprintChanged;
 1035: public void Dispose()
 1048: public sealed record FootprintPreset(
 1080: public TradePrint? Synthesize(Quote q)
 1092: public void Reset() => _prev = null;
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs
```cs
   24: public partial class VolumeFootprintWindow : MetroWindow
  113: public VolumeFootprintWindow()
```
