# TradingTerminal.Backtest — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.Backtest/AvaloniaUi/BacktestAvaloniaWindow.axaml.cs
```cs
   12: public partial class BacktestAvaloniaWindow : Window
   16: public BacktestAvaloniaWindow()
```

## src/linux/Tools/TradingTerminal.Backtest/BacktestServiceCollectionExtensions.cs
```cs
    9: public static class BacktestServiceCollectionExtensions
   11: public static IServiceCollection AddBacktestSurface(this IServiceCollection services)
```

## src/linux/Tools/TradingTerminal.Backtest/BacktestView.xaml.cs
```cs
    6: public partial class BacktestView : UserControl
    8: public BacktestView()
```

## src/linux/Tools/TradingTerminal.Backtest/BacktestViewModel.cs
```cs
   24: public sealed partial class BacktestViewModel : ViewModelBase
   31: public BacktestViewModel(
   46: public ObservableCollection<BacktestStrategyOption> Strategies { get; }
   47: public ObservableCollection<Trade> Trades { get; }
   48: public ObservableCollection<EquityPoint> EquityCurve { get; }
   75: public bool IsFastAvailable =>
   79: public event EventHandler? EquityCurveUpdated;
   82: public async Task BrowseDataPath()
   90: public async Task RunAsync()
  177: public void Cancel()
```

## src/linux/Tools/TradingTerminal.Backtest/QuickBacktestView.xaml.cs
```cs
    5: public partial class QuickBacktestView : UserControl
    7: public QuickBacktestView()
```

## src/linux/Tools/TradingTerminal.Backtest/QuickBacktestViewModel.cs
```cs
   22: public enum QuickBacktestDataMode
   45: public sealed partial class QuickBacktestViewModel : ViewModelBase, IDisposable
   55: public QuickBacktestViewModel(
   98: public ObservableCollection<SignalInstrument> Instruments { get; }
   99: public ObservableCollection<BarSize> BarSizes { get; }
  100: public ObservableCollection<LookbackOption> Lookbacks { get; }
  101: public ObservableCollection<QuickBacktestDataMode> DataModes { get; }
  102: public ObservableCollection<BrokerKind> Brokers { get; }
  103: public ObservableCollection<Trade> Trades { get; }
  104: public ObservableCollection<EquityPoint> EquityCurve { get; }
  131: public bool IsFullTape => SelectedDataMode == QuickBacktestDataMode.FullTapeRealTrades;
  132: public bool IsBarSynthetic => SelectedDataMode == QuickBacktestDataMode.BarSynthetic;
  143: public event EventHandler? EquityCurveUpdated;
  151: public bool Initialize(string? backtestStrategyId, string displayName, bool preferFullTape)
  214: public async Task RunAsync()
  342: public void Cancel() => _runCts?.Cancel();
  345: public void Dispose()
  423: public sealed record LookbackOption(string Label, TimeSpan Duration)
  425: public override string ToString() => Label;
```
