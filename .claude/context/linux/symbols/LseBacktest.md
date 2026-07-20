# TradingTerminal.LseBacktest — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.LseBacktest/AvaloniaUi/LseBacktestAvaloniaWindow.axaml.cs
```cs
    8: public partial class LseBacktestAvaloniaWindow : Window
   10: public LseBacktestAvaloniaWindow() => InitializeComponent();
```

## src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestServiceCollectionExtensions.cs
```cs
   11: public static class LseBacktestServiceCollectionExtensions
   13: public static IServiceCollection AddLseBacktestSurface(this IServiceCollection services)
```

## src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestView.xaml.cs
```cs
    6: public partial class LseBacktestView : UserControl
    8: public LseBacktestView()
```

## src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestViewModel.cs
```cs
   32: public sealed partial class LseBacktestViewModel : ViewModelBase
   41: public LseBacktestViewModel(
   60: public ObservableCollection<BacktestStrategyOption> Strategies { get; }
   61: public ObservableCollection<BarSize> BarSizes { get; }
   62: public ObservableCollection<Trade> Trades { get; }
   63: public ObservableCollection<EquityPoint> EquityCurve { get; }
   81: public event EventHandler? EquityCurveUpdated;
   84: public async Task RunAsync()
  179: public void Cancel() => _runCts?.Cancel();
```
