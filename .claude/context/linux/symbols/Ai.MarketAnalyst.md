# TradingTerminal.Ai.MarketAnalyst — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AiAnalystView.xaml.cs
```cs
    5: public partial class AiAnalystView : UserControl
    7: public AiAnalystView()
```

## src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AiAnalystViewModel.cs
```cs
   22: public sealed partial class AiAnalystViewModel : ViewModelBase
   32: public AiAnalystViewModel(
   74: public ObservableCollection<AnalystReport> History { get; }
   75: public ObservableCollection<string> Timeframes { get; }
   77: public bool IsAvailable => _analyst.IsAvailable;
   87: public async Task AnalyzeAsync()
  146: public void Cancel() => _runCts?.Cancel();
  149: public void ClearHistory() => History.Clear();
```

## src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AvaloniaUi/AiAnalystAvaloniaWindow.axaml.cs
```cs
    8: public partial class AiAnalystAvaloniaWindow : Window
   10: public AiAnalystAvaloniaWindow() => InitializeComponent();
```

## src/linux/AI/TradingTerminal.Ai.MarketAnalyst/MarketAnalystServiceCollectionExtensions.cs
```cs
    7: public static class MarketAnalystServiceCollectionExtensions
    9: public static IServiceCollection AddMarketAnalyst(this IServiceCollection services)
```
