# TradingTerminal.Strategies.CumulativeDelta — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/AvaloniaUi/CumulativeDeltaAvaloniaWindow.axaml.cs
```cs
   10: public partial class CumulativeDeltaAvaloniaWindow : Window
   12: public CumulativeDeltaAvaloniaWindow() => InitializeComponent();
```

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaPlugin.cs
```cs
   13: public sealed class CumulativeDeltaPlugin : IStrategyPlugin
   15: public string Name => "Cumulative Delta Scalper";
   17: public string TargetSdkVersion => SdkInfo.Version;
   19: public void Register(IPluginRegistrar registrar) => registrar.Services.AddCumulativeDeltaStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaStrategy.cs
```cs
    5: public sealed class CumulativeDeltaStrategy : ITradingStrategy
    7: public string Id => "cumulative.delta.scalper";
    8: public string DisplayName => "Cumulative Delta Scalper";
    9: public string Description =>
   20: public StrategyDataRequirement DataRequirement =>
```

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaViewModel.cs
```cs
   45: public sealed partial class CumulativeDeltaViewModel : ViewModelBase, IDisposable
   47: public const int MaxBarsRetained = 300;
   48: public const int MaxWindowSize = 50;
   49: public const int MaxSpreadHistorySize = 200;
   52: public const int MaxInstrumentsDisplayed = 500;
   83: public IReadOnlyList<FootprintBar> FootprintBars => _footprintBars;
   84: public event EventHandler? FootprintChanged;
  113: public CumulativeDeltaViewModel(
  196: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  204: public IReadOnlyList<BarSize> TimeframeOptions { get; }
  269: public ObservableCollection<Bar> Bars { get; }
  270: public ObservableCollection<int> BarDeltas { get; }
  271: public ObservableCollection<string> RecentSignals { get; }
  275: public ObservableCollection<DeltaPoint> DeltaPoints { get; } = new();
  278: public ObservableCollection<GateRow> Gates { get; } = new();
  281: public ObservableCollection<GateRow> Confirmations { get; } = new();
  290: public int MaxConfirmations => _sawRealTape ? 6 : 5;
  321: public event EventHandler? BarsChanged;
  322: public event EventHandler? DeltasChanged;
  367: public ObservableCollection<string> PresetNames { get; } = new();
  506: public async Task StartStreamAsync()
  551: public async Task StopStreamAsync()
  566: public void Dispose()
 1318: public sealed record GateRow(string Name, bool Pass, string Detail);
 1321: public sealed record DeltaPoint(DateTime TimeUtc, double BarDelta, double WindowCum);
 1323: public enum SessionId
 1335: public sealed record CumulativeDeltaPreset(
```

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaWindow.xaml.cs
```cs
   11: public partial class CumulativeDeltaWindow : MetroWindow
   42: public CumulativeDeltaWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddCumulativeDeltaStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/Indicators.cs
```cs
   14: public static double Atr(IReadOnlyList<Bar> bars, int period)
   37: public static double Ema(IReadOnlyList<Bar> bars, int period)
   57: public static double Adx(IReadOnlyList<Bar> bars, int period)
```
