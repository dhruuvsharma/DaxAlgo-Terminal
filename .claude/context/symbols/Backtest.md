# TradingTerminal.Backtest — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/TradingTerminal.Backtest/AvaloniaUi/BacktestAvaloniaWindow.axaml.cs
```cs
   12: public partial class BacktestAvaloniaWindow : Window
   16: public BacktestAvaloniaWindow()
```

## src/windows/Tools/TradingTerminal.Backtest/BacktestServiceCollectionExtensions.cs
```cs
    9: public static class BacktestServiceCollectionExtensions
   11: public static IServiceCollection AddBacktestSurface(this IServiceCollection services)
```

## src/windows/Tools/TradingTerminal.Backtest/BacktestView.xaml.cs
```cs
    8: public partial class BacktestView : UserControl
   10: public BacktestView()
```

## src/windows/Tools/TradingTerminal.Backtest/BacktestViewModel.cs
```cs
   26: public sealed partial class BacktestViewModel : ViewModelBase
   33: public BacktestViewModel(
   53: public ObservableCollection<string> PresetNames { get; }
  149: public ObservableCollection<BacktestStrategyOption> Strategies { get; }
  150: public ObservableCollection<Trade> Trades { get; }
  151: public ObservableCollection<EquityPoint> EquityCurve { get; }
  178: public bool IsFastAvailable =>
  182: public event EventHandler? EquityCurveUpdated;
  185: public async Task BrowseDataPath()
  193: public async Task RunAsync()
  280: public void Cancel()
  289: public sealed record BacktestRunPreset(
```

## src/windows/Tools/TradingTerminal.Backtest/QuickBacktestView.xaml.cs
```cs
    5: public partial class QuickBacktestView : UserControl
    7: public QuickBacktestView()
```

## src/windows/Tools/TradingTerminal.Backtest/QuickBacktestViewModel.cs
```cs
   22: public enum QuickBacktestDataMode
   45: public sealed partial class QuickBacktestViewModel : ViewModelBase, IDisposable
   65: public QuickBacktestViewModel(
  112: public ObservableCollection<SignalInstrument> Instruments { get; }
  113: public ObservableCollection<BarSize> BarSizes { get; }
  114: public ObservableCollection<LookbackOption> Lookbacks { get; }
  115: public ObservableCollection<QuickBacktestDataMode> DataModes { get; }
  116: public ObservableCollection<BrokerKind> Brokers { get; }
  117: public ObservableCollection<Trade> Trades { get; }
  118: public ObservableCollection<EquityPoint> EquityCurve { get; }
  146: public bool IsFullTape => SelectedDataMode == QuickBacktestDataMode.FullTapeRealTrades;
  147: public bool IsBarSynthetic => SelectedDataMode == QuickBacktestDataMode.BarSynthetic;
  165: public event EventHandler? EquityCurveUpdated;
  173: public bool Initialize(string? backtestStrategyId, string displayName, bool preferFullTape)
  262: public async Task RunAsync()
  390: public void Cancel() => _runCts?.Cancel();
  393: public void Dispose()
  473: public sealed record LookbackOption(string Label, TimeSpan Duration)
  475: public override string ToString() => Label;
```
