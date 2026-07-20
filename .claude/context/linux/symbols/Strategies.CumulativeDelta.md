# TradingTerminal.Strategies.CumulativeDelta — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.CumulativeDelta/AvaloniaUi/CumulativeDeltaAvaloniaWindow.axaml.cs
```cs
   10: public partial class CumulativeDeltaAvaloniaWindow : Window
   12: public CumulativeDeltaAvaloniaWindow() => InitializeComponent();
```

## src/linux/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaStrategy.cs
```cs
    5: public sealed class CumulativeDeltaStrategy : ITradingStrategy
    7: public string Id => "cumulative.delta.scalper";
    8: public string DisplayName => "Cumulative Delta Scalper";
    9: public string Description =>
   20: public StrategyDataRequirement DataRequirement =>
```

## src/linux/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaViewModel.cs
```cs
   41: public sealed partial class CumulativeDeltaViewModel : ViewModelBase, IDisposable
   43: public const int MaxBarsRetained = 300;
   44: public const int MaxWindowSize = 50;
   45: public const int MaxSpreadHistorySize = 200;
   48: public const int MaxInstrumentsDisplayed = 500;
   79: public IReadOnlyList<FootprintBar> FootprintBars => _footprintBars;
   80: public event EventHandler? FootprintChanged;
  105: public CumulativeDeltaViewModel(
  195: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  203: public IReadOnlyList<BarSize> TimeframeOptions { get; }
  268: public ObservableCollection<Bar> Bars { get; }
  269: public ObservableCollection<int> BarDeltas { get; }
  270: public ObservableCollection<string> RecentSignals { get; }
  274: public ObservableCollection<DeltaPoint> DeltaPoints { get; } = new();
  277: public ObservableCollection<GateRow> Gates { get; } = new();
  280: public ObservableCollection<GateRow> Confirmations { get; } = new();
  289: public int MaxConfirmations => _sawRealTape ? 6 : 5;
  320: public event EventHandler? BarsChanged;
  321: public event EventHandler? DeltasChanged;
  362: public async Task StartStreamAsync()
  407: public async Task StopStreamAsync()
  422: public void Dispose()
 1172: public sealed record GateRow(string Name, bool Pass, string Detail);
 1175: public sealed record DeltaPoint(DateTime TimeUtc, double BarDelta, double WindowCum);
 1177: public enum SessionId
```

## src/linux/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaWindow.xaml.cs
```cs
   11: public partial class CumulativeDeltaWindow : MetroWindow
   42: public CumulativeDeltaWindow()
```

## src/linux/Strategies/TradingTerminal.Strategies.CumulativeDelta/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddCumulativeDeltaStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.CumulativeDelta/Indicators.cs
```cs
   14: public static double Atr(IReadOnlyList<Bar> bars, int period)
   37: public static double Ema(IReadOnlyList<Bar> bars, int period)
   57: public static double Adx(IReadOnlyList<Bar> bars, int period)
```
