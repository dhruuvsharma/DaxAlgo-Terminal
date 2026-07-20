# TradingTerminal.Correlation — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.Correlation/AvaloniaUi/LiveCorrelationAvaloniaWindow.axaml.cs
```cs
   11: public partial class LiveCorrelationAvaloniaWindow : Window
   15: public LiveCorrelationAvaloniaWindow()
```

## src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixControl.cs
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

## src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixViewModel.cs
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

## src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixWindow.xaml.cs
```cs
   13: public partial class CorrelationMatrixWindow : MetroWindow
   15: public CorrelationMatrixWindow()
```

## src/linux/Tools/TradingTerminal.Correlation/CorrelationPickerViewModelBase.cs
```cs
   27: public abstract partial class CorrelationPickerViewModelBase : ViewModelBase
   29: protected const string AllCategories = "All categories";
   37: protected IMarketDataRepository Repository { get; }
   38: protected IBrokerSelector Selector { get; }
   39: protected ILogger Logger { get; }
   43: protected IReadOnlyList<SelectableInstrument> AllInstruments { get; private set; } = Array.Empty<SelectableInstrument>();
   45: protected CorrelationPickerViewModelBase(
   69: public ObservableCollection<SelectableInstrument> Instruments { get; }
   71: public ICollectionView InstrumentsView { get; }
   73: public ObservableCollection<string> Categories { get; }
   90: public int SelectedCount => AllInstruments.Count(i => i.IsSelected);
   93: protected IReadOnlyList<SelectableInstrument> SelectedInstruments =>
  194: protected void CleanupInstruments()
  218: protected void BuildMatrix(CorrelationMatrix result)
  227: protected static IReadOnlyList<string> LabelFor(IReadOnlyList<SelectableInstrument> instruments)
  239: public sealed partial class SelectableInstrument : ObservableObject
  241: public SelectableInstrument(string displayName, string category, Contract contract, BrokerKind broker)
  253: public string DisplayName { get; }
  254: public string RawCategory { get; }
  255: public Contract Contract { get; }
  256: public BrokerKind Broker { get; }
  258: public string CanonicalCategory { get; }
  259: public int CategoryOrder { get; }
  260: public string BrokerAbbrev { get; }
  262: public string Symbol => Contract.Symbol;
  266: public event EventHandler? SelectionChanged;
  279: public const string Crypto = "Crypto";
  280: public const string Fx = "FX";
  281: public const string Commodities = "Commodities";
  282: public const string Indices = "Indices";
  283: public const string Etfs = "ETFs";
  284: public const string Stocks = "Stocks";
  285: public const string Futures = "Futures";
  286: public const string Other = "Other";
  297: public static int OrderOf(string category)
  303: public static string Classify(string? rawCategory, Contract? contract)
  328: public static string BrokerAbbrev(BrokerKind broker) => broker switch
```

## src/linux/Tools/TradingTerminal.Correlation/CorrelationServiceCollectionExtensions.cs
```cs
    7: public static class CorrelationServiceCollectionExtensions
    9: public static IServiceCollection AddCorrelationSurface(this IServiceCollection services)
```

## src/linux/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixViewModel.cs
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

## src/linux/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixWindow.xaml.cs
```cs
   13: public partial class LiveCorrelationMatrixWindow : MetroWindow
   15: public LiveCorrelationMatrixWindow()
```
