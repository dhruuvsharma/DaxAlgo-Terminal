# TradingTerminal.VolumeFootprint — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintModels.cs
```cs
    7: public enum PocSeries { Total, Buy, Sell }
   17: public enum CellDisplayMode { BidAsk, Delta, Volume }
   22: public sealed record PocFitCurve(CurveFitKind Kind, PocSeries Series, IReadOnlyList<double> Prices);
   26: public sealed record PredictedBar(double Poc, double BuyPoc, double SellPoc);
   31: public sealed record MlPredictedBar(
   51: public sealed class RenderBar
   56: public RenderBar(FootprintBar core)
  108: public FootprintBar Core { get; }
  112: public DateTime StartUtc      => Core.StartUtc;
  113: public long     TotalVolume   => Core.TotalVolume;
  114: public long     Delta         => Core.Delta;
  115: public long     CumulativeDelta => Core.CumulativeDelta;
  118: public double PointOfControl  => Core.PocPrice;
  122: public double BuyPointOfControl  { get; }
  125: public double SellPointOfControl { get; }
  128: public double ValueAreaHigh { get; }
  131: public double ValueAreaLow { get; }
  134: public long MaxCellVolume { get; }
  137: public int StackedBuy => Core.StackedBuy;
  140: public int StackedSell => Core.StackedSell;
  143: public IReadOnlyList<Core.MarketData.FootprintFeatureRow> Cells => Core.Rows;
  146: public FeedQuality Quality => Core.Quality;
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintPanel.xaml.cs
```cs
   27: public partial class VolumeFootprintPanel : UserControl
   31: public static readonly DependencyProperty FeaturesProperty = DependencyProperty.Register(
   35: public VolumeFootprintPanelFeatures Features
  128: public VolumeFootprintPanel()
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintPanelFeatures.cs
```cs
   17: public sealed record VolumeFootprintPanelFeatures
   22: public bool Toolbar { get; init; } = true;
   25: public bool Stats { get; init; } = true;
   28: public bool Legend { get; init; } = true;
   32: public bool Regression { get; init; } = true;
   37: public bool MlForecast { get; init; } = true;
   40: public bool Imbalances { get; init; } = true;
   43: public bool ValueArea { get; init; } = true;
   46: public bool VolumeProfile { get; init; } = true;
   49: public static VolumeFootprintPanelFeatures Full { get; } = new();
   53: public static VolumeFootprintPanelFeatures ChartOnly { get; } = new()
   67: public static VolumeFootprintPanelFeatures Embedded { get; } = new()
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintServiceCollectionExtensions.cs
```cs
   11: public static class VolumeFootprintServiceCollectionExtensions
   13: public static IServiceCollection AddFootprintSurface(this IServiceCollection services)
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs
```cs
   31: public sealed partial class VolumeFootprintViewModel : ViewModelBase, IDisposable
   33: public const int MaxInstrumentsDisplayed = 500;
  126: public sealed record FootprintInterval(string Label, TimeSpan Span);
  136: public VolumeFootprintViewModel(
  191: public ObservableCollection<SignalInstrument> Instruments { get; }
  192: public ObservableCollection<FootprintInterval> Intervals { get; }
  195: public IReadOnlyList<CellDisplayMode> DisplayModes { get; } =
  199: public ObservableCollection<RenderBar> Bars { get; }
  202: public ObservableCollection<string> PresetNames { get; }
  278: public bool MlEnabled { get; set; } = true;
  282: public IReadOnlyList<LearnerOption> Learners => Forecasters.DirectionChoices;
  299: public IReadOnlyList<PocFitCurve> FitCurves { get; private set; } = Array.Empty<PocFitCurve>();
  303: public IReadOnlyList<PredictedBar> Predicted { get; private set; } = Array.Empty<PredictedBar>();
  308: public IReadOnlyList<MlPredictedBar> MlPredicted { get; private set; } = Array.Empty<MlPredictedBar>();
  312: public double PocSlope { get; private set; }
  315: public double PocIntercept { get; private set; }
  318: public bool HasRegression { get; private set; }
  321: public double BuyPocSlope { get; private set; }
  322: public double BuyPocIntercept { get; private set; }
  323: public bool HasBuyRegression { get; private set; }
  326: public double SellPocSlope { get; private set; }
  327: public double SellPocIntercept { get; private set; }
  328: public bool HasSellRegression { get; private set; }
  331: public event EventHandler? FootprintChanged;
 1204: public void Dispose()
 1224: public sealed record VolumeFootprintEmbedOptions(SignalInstrument? Instrument = null, bool MlEnabled = false);
 1229: public sealed record FootprintPreset(
 1263: public TradePrint? Synthesize(Quote q)
 1275: public void Reset() => _prev = null;
```

## src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs
```cs
   11: public partial class VolumeFootprintWindow : MetroWindow
   13: public VolumeFootprintWindow() => InitializeComponent();
```
