# TradingTerminal.App.Avalonia — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Shell/TradingTerminal.App.Avalonia/App.axaml.cs
```cs
   17: public partial class App : Application
   20: public IServiceProvider? Services { get; private set; }
   22: public override void Initialize() => AvaloniaXamlLoader.Load(this);
   24: public override void OnFrameworkInitializationCompleted()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/AvaloniaUiDispatcher.cs
```cs
   14: public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
   16: public void Post(Action action) => Dispatcher.UIThread.Post(action);
   18: public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Charts/ChartsViewModel.cs
```cs
   12: public sealed partial class ChartsViewModel : ObservableObject
   17: public IReadOnlyList<double> Open { get; }
   18: public IReadOnlyList<double> High { get; }
   19: public IReadOnlyList<double> Low { get; }
   20: public IReadOnlyList<double> Close { get; }
   22: public ChartsViewModel()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Charts/ChartsWindow.axaml.cs
```cs
   13: public partial class ChartsWindow : Window
   15: public ChartsWindow()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Charts/LineChartControl.cs
```cs
   12: public sealed class LineChartControl : Control
   21: public static readonly DirectProperty<LineChartControl, IReadOnlyList<double>> SeriesProperty =
   26: public IReadOnlyList<double> Series
   32: public static readonly DirectProperty<LineChartControl, IReadOnlyList<double>> Series2Property =
   38: public IReadOnlyList<double> Series2
   44: public override void Render(DrawingContext ctx)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Composition/ServiceConfiguration.cs
```cs
   43: public static class ServiceConfiguration
   45: public static IServiceProvider Build()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Login/LoginViewModel.cs
```cs
   16: public sealed partial class LoginViewModel : ObservableObject
   20: public event Action<BrokerKind>? Connected;
   22: public LoginViewModel(IBrokerSelector selector)
   34: public ObservableCollection<BrokerOption> Brokers { get; }
   76: public sealed record BrokerOption(BrokerKind Kind, string DisplayName, string Description, bool IsLive);
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Login/LoginWindow.axaml.cs
```cs
    5: public partial class LoginWindow : Window
    7: public LoginWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchViewModel.cs
```cs
   12: public sealed partial class ArimaGarchViewModel : ObservableObject
   17: public ArimaGarchViewModel()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchWindow.axaml.cs
```cs
    5: public partial class ArimaGarchWindow : Window
    7: public ArimaGarchWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanViewModel.cs
```cs
   13: public sealed partial class KalmanViewModel : ObservableObject
   17: public KalmanViewModel()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanWindow.axaml.cs
```cs
    5: public partial class KalmanWindow : Window
    7: public KalmanWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityViewModel.cs
```cs
   13: public sealed partial class StationarityViewModel : ObservableObject
   17: public StationarityViewModel()
   29: public SeriesTransform[] Transforms { get; }
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityWindow.axaml.cs
```cs
    5: public partial class StationarityWindow : Window
    7: public StationarityWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Program.cs
```cs
    9: public static void Main(string[] args) =>
   12: public static AppBuilder BuildAvaloniaApp() =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveActivityWindow.axaml.cs
```cs
    5: public partial class ArchiveActivityWindow : Window
    7: public ArchiveActivityWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveSettingsWindow.axaml.cs
```cs
    5: public partial class ArchiveSettingsWindow : Window
    7: public ArchiveSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/NotificationsSettingsWindow.axaml.cs
```cs
    5: public partial class NotificationsSettingsWindow : Window
    7: public NotificationsSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ResearchSettingsWindow.axaml.cs
```cs
    5: public partial class ResearchSettingsWindow : Window
    7: public ResearchSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/StrategyAuthoringWindow.axaml.cs
```cs
    5: public partial class StrategyAuthoringWindow : Window
    7: public StrategyAuthoringWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/SupportWindow.axaml.cs
```cs
    6: public partial class SupportWindow : Window
    8: public SupportWindow()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/BrokerApiChipViewModel.cs
```cs
   12: public sealed partial class BrokerApiChipViewModel : ViewModelBase
   14: public BrokerApiChipViewModel(BrokerKind broker)
   20: public BrokerKind Broker { get; }
   22: public string Label { get; }
   28: public int AvailableCallsPerMinute => SoftLimitPerMinute > 0
   32: public BrokerApiChipStatus Status => SoftLimitPerMinute <= 0
   41: public string UsageDisplay => SoftLimitPerMinute > 0
   45: public string TooltipText => SoftLimitPerMinute > 0
   78: public enum BrokerApiChipStatus
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/BrokerApiMeterViewModel.cs
```cs
   15: public sealed partial class BrokerApiMeterViewModel : ViewModelBase, IDisposable
   21: public BrokerApiMeterViewModel(IBrokerApiMeter meter)
   31: public ObservableCollection<BrokerApiChipViewModel> Chips { get; }
   71: public void Dispose() => _timer.Stop();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindow.axaml.cs
```cs
   12: public partial class MainWindow : Window
   14: public MainWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindowViewModel.cs
```cs
   21: public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
   26: public MainWindowViewModel(
   54: public MainWindowViewModel()
   68: public ObservableCollection<ITradingStrategy> Strategies { get; }
   73: public string SelectedDetails => SelectedStrategy?.Description ?? "Select a strategy to see its description.";
   79: public BrokerApiMeterViewModel? ApiMeter { get; }
   83: public InMemoryLogSink ActivityLog { get; }
   86: public ObservableCollection<LogEntry> VisibleLog { get; }
  132: public bool IsDisconnected => ConnectionState is not ConnectionState.Connected;
  133: public string DisconnectBannerText => "Disconnected — connect a broker to resume";
  134: public int ConnectedBrokerCount => _brokerSelector?.Connected.Count ?? 0;
  138: public string RuntimeInfo =>
  158: public async Task ReconnectAllAsync()
  184: public void Dispose()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/ShellConverters.cs
```cs
   13: public static readonly IBrush Bullish = Brush("#00C853");
   14: public static readonly IBrush Warning = Brush("#FFD600");
   15: public static readonly IBrush Danger = Brush("#FF1744");
   16: public static readonly IBrush Muted = Brush("#8A8A8A");
   17: public static readonly IBrush Accent = Brush("#FF8C00");
   18: public static readonly IBrush BullishSoft = Brush("#2600C853");
   19: public static readonly IBrush WarningSoft = Brush("#26FFD600");
   20: public static readonly IBrush BearishSoft = Brush("#26FF1744");
   21: public static readonly IBrush Neutral = Brush("#1AFFFFFF");
   22: public static readonly IBrush NeutralStrong = Brush("#22FFFFFF");
   23: public static readonly IBrush BorderStrong = Brush("#3A3A3A");
   25: public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
   29: public sealed class ConnectionStateToBrushConverter : IValueConverter
   31: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   41: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   46: public sealed class ApiStatusToBrushConverter : IValueConverter
   48: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   59: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   65: public sealed class SessionFlagToBrushConverter : IValueConverter
   67: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   78: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   84: public sealed class BoolToTextConverter : IValueConverter
   86: public string TrueText { get; set; } = string.Empty;
   87: public string FalseText { get; set; } = string.Empty;
   89: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   92: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   98: public sealed class LogLevelToBrushConverter : IValueConverter
  100: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
  113: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/StrategyPillConverters.cs
```cs
   13: public sealed record StrategyPill(string Text, IBrush Background, IBrush Foreground);
   20: public sealed class StrategyDataRequirementConverter : IValueConverter
   32: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   51: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   62: public sealed class StrategyClassificationConverter : IValueConverter
   74: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   98: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  135: public sealed class StringNotEmptyConverter : IValueConverter
  137: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  140: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationHeatmapControl.cs
```cs
   13: public sealed class CorrelationHeatmapControl : Control
   27: public static readonly DirectProperty<CorrelationHeatmapControl, CorrelationMatrix?> MatrixProperty =
   32: public CorrelationMatrix? Matrix
   38: protected override Size MeasureOverride(Size a)
   44: public override void Render(DrawingContext ctx)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationViewModel.cs
```cs
   13: public sealed partial class CorrelationViewModel : ObservableObject
   17: public CorrelationViewModel() => Run();
   51: public static double NextGaussian(this Random r)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationWindow.axaml.cs
```cs
    5: public partial class CorrelationWindow : Window
    7: public CorrelationWindow() => InitializeComponent();
```
