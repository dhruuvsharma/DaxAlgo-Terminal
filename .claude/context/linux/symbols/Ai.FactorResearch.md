# TradingTerminal.Ai.FactorResearch — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/AI/TradingTerminal.Ai.FactorResearch/AvaloniaUi/FactorResearchAvaloniaWindow.axaml.cs
```cs
    8: public partial class FactorResearchAvaloniaWindow : Window
   10: public FactorResearchAvaloniaWindow() => InitializeComponent();
```

## src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchServiceCollectionExtensions.cs
```cs
    6: public static class FactorResearchServiceCollectionExtensions
    8: public static IServiceCollection AddFactorResearch(this IServiceCollection services)
```

## src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchView.xaml.cs
```cs
    5: public partial class FactorResearchView : UserControl
    7: public FactorResearchView() { InitializeComponent(); }
```

## src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchViewModel.cs
```cs
   20: public sealed partial class FactorResearchViewModel : ViewModelBase
   25: public FactorResearchViewModel(ILogger<FactorResearchViewModel> logger)
   32: public IReadOnlyList<string> FeatureChoices { get; }
   33: public ObservableCollection<CorrelationRow> Correlations { get; } = new();
   34: public ObservableCollection<FactorComputation.DecileRow> Deciles { get; } = new();
  116: public sealed class CorrelationRow
  118: public string Feature { get; set; } = "";
  119: public ObservableCollection<CorrelationCell> Cells { get; } = new();
  122: public sealed record CorrelationCell(string Other, double Value)
  124: public string Display => Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
```
