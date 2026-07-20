# TradingTerminal.Ai.MlFeatures — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/AI/TradingTerminal.Ai.MlFeatures/AvaloniaUi/MlFeaturesAvaloniaWindow.axaml.cs
```cs
    7: public partial class MlFeaturesAvaloniaWindow : Window
    9: public MlFeaturesAvaloniaWindow() => InitializeComponent();
```

## src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesServiceCollectionExtensions.cs
```cs
    6: public static class MlFeaturesServiceCollectionExtensions
    8: public static IServiceCollection AddMlFeatures(this IServiceCollection services)
```

## src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesView.xaml.cs
```cs
    5: public partial class MlFeaturesView : UserControl
    7: public MlFeaturesView()
```

## src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesViewModel.cs
```cs
   21: public sealed partial class MlFeaturesViewModel : ViewModelBase
   26: public MlFeaturesViewModel(ILogger<MlFeaturesViewModel> logger)
   31: public ObservableCollection<LabelPreviewRow> Preview { get; } = new();
  182: public sealed record LabelPreviewRow(
```
