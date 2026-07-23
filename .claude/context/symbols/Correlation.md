# TradingTerminal.Correlation — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixControl.cs
```cs
   19: public sealed class CorrelationMatrixControl : FrameworkElement
   40: public static readonly DependencyProperty MatrixProperty = DependencyProperty.Register(
   45: public CorrelationMatrix? Matrix
   51: protected override Size MeasureOverride(Size availableSize)
   59: protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
   68: protected override void OnRender(DrawingContext dc)
  118: protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
  134: protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
```

## src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixViewModel.cs
```cs
   21: public sealed partial class CorrelationMatrixViewModel : CorrelationPickerViewModelBase, IDisposable
   47: public CorrelationMatrixViewModel(
   60: public ObservableCollection<TimeframeOption> Timeframes { get; }
   61: public ObservableCollection<LookbackOption> Lookbacks { get; }
  218: public void Dispose()
  234: public sealed record TimeframeOption(string Label, BarSize BarSize)
  236: public override string ToString() => Label;
  240: public sealed record LookbackOption(string Label, TimeSpan Duration)
  242: public override string ToString() => Label;
```

## src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixWindow.xaml.cs
```cs
   15: public partial class CorrelationMatrixWindow : MetroWindow
   17: public CorrelationMatrixWindow()
```

## src/windows/Tools/TradingTerminal.Correlation/CorrelationPickerViewModelBase.cs
```cs
   30: public abstract partial class CorrelationPickerViewModelBase : ViewModelBase
   32: protected const string AllCategories = "All categories";
   40: protected IMarketDataRepository Repository { get; }
   41: protected IBrokerSelector Selector { get; }
   42: protected ILogger Logger { get; }
   46: protected IReadOnlyList<SelectableInstrument> AllInstruments { get; private set; } = Array.Empty<SelectableInstrument>();
   48: protected CorrelationPickerViewModelBase(
   72: public ObservableCollection<SelectableInstrument> Instruments { get; }
   74: public ICollectionView InstrumentsView { get; }
   76: public ObservableCollection<string> Categories { get; }
   93: public int SelectedCount => AllInstruments.Count(i => i.IsSelected);
   96: protected IReadOnlyList<SelectableInstrument> SelectedInstruments =>
  197: protected void CleanupInstruments()
  221: protected void BuildMatrix(CorrelationMatrix result)
  262: protected static IReadOnlyList<string> LabelFor(IReadOnlyList<SelectableInstrument> instruments)
  274: public sealed partial class SelectableInstrument : ObservableObject
  276: public SelectableInstrument(string displayName, string category, Contract contract, BrokerKind broker)
  288: public string DisplayName { get; }
  289: public string RawCategory { get; }
  290: public Contract Contract { get; }
  291: public BrokerKind Broker { get; }
  293: public string CanonicalCategory { get; }
  294: public int CategoryOrder { get; }
  295: public string BrokerAbbrev { get; }
  297: public string Symbol => Contract.Symbol;
  301: public event EventHandler? SelectionChanged;
  314: public const string Crypto = "Crypto";
  315: public const string Fx = "FX";
  316: public const string Commodities = "Commodities";
  317: public const string Indices = "Indices";
  318: public const string Etfs = "ETFs";
  319: public const string Stocks = "Stocks";
  320: public const string Futures = "Futures";
  321: public const string Other = "Other";
  332: public static int OrderOf(string category)
  338: public static string Classify(string? rawCategory, Contract? contract)
  363: public static string BrokerAbbrev(BrokerKind broker) => broker switch
```

## src/windows/Tools/TradingTerminal.Correlation/CorrelationServiceCollectionExtensions.cs
```cs
    7: public static class CorrelationServiceCollectionExtensions
    9: public static IServiceCollection AddCorrelationSurface(this IServiceCollection services)
```

## src/windows/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixViewModel.cs
```cs
   27: public sealed partial class LiveCorrelationMatrixViewModel : CorrelationPickerViewModelBase, IDisposable
   58: public LiveCorrelationMatrixViewModel(
   76: public ObservableCollection<SampleIntervalOption> SampleIntervals { get; }
   77: public ObservableCollection<WindowOption> Windows { get; }
  220: public void Dispose()
  230: public SelectableInstrument Instrument { get; } = instrument;
  231: public InstrumentId Id { get; } = id;
  232: public Queue<double> Closes { get; } = new();
  237: public sealed record SampleIntervalOption(string Label, TimeSpan Interval)
  239: public override string ToString() => Label;
  243: public sealed record WindowOption(string Label, int Count)
  245: public override string ToString() => Label;
```

## src/windows/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixWindow.xaml.cs
```cs
   15: public partial class LiveCorrelationMatrixWindow : MetroWindow
   17: public LiveCorrelationMatrixWindow()
```
