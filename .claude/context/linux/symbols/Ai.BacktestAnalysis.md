# TradingTerminal.Ai.BacktestAnalysis — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/AvaloniaUi/BacktestAnalysisAvaloniaWindow.axaml.cs
```cs
    7: public partial class BacktestAnalysisAvaloniaWindow : Window
    9: public BacktestAnalysisAvaloniaWindow() => InitializeComponent();
```

## src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisServiceCollectionExtensions.cs
```cs
    6: public static class BacktestAnalysisServiceCollectionExtensions
    8: public static IServiceCollection AddBacktestAnalysis(this IServiceCollection services)
```

## src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisView.xaml.cs
```cs
    5: public partial class BacktestAnalysisView : UserControl
    7: public BacktestAnalysisView()
```

## src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisViewModel.cs
```cs
   22: public sealed partial class BacktestAnalysisViewModel : ViewModelBase
   26: public BacktestAnalysisViewModel(ILogger<BacktestAnalysisViewModel> logger)
   33: public IReadOnlyList<string> StrategyChoices { get; }
   34: public ObservableCollection<WalkForwardRow> WalkForwardRows { get; } = new();
  279: public sealed record WalkForwardRow(
```
