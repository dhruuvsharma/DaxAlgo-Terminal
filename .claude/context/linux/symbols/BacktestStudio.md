# TradingTerminal.BacktestStudio — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.BacktestStudio/AvaloniaUi/BacktestStudioAvaloniaWindow.axaml.cs
```cs
    9: public partial class BacktestStudioAvaloniaWindow : Window
   11: public BacktestStudioAvaloniaWindow() => InitializeComponent();
```

## src/linux/Tools/TradingTerminal.BacktestStudio/AxisRowViewModel.cs
```cs
    8: public sealed partial class AxisRowViewModel : ObservableObject
   10: public AxisRowViewModel(ParameterDescriptor descriptor)
   18: public ParameterDescriptor Descriptor { get; }
   19: public string Name => Descriptor.Name;
   20: public string Label => Descriptor.Label;
   27: public ParameterAxis ToAxis() => ParameterAxis.Range(Name, Min, Max, Step);
```

## src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioServiceCollectionExtensions.cs
```cs
   14: public static class BacktestStudioServiceCollectionExtensions
   16: public static IServiceCollection AddBacktestStudioSurface(this IServiceCollection services)
```

## src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioView.xaml.cs
```cs
   12: public partial class BacktestStudioView : UserControl
   18: public BacktestStudioView()
```

## src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioViewModel.cs
```cs
   25: public sealed partial class BacktestStudioViewModel : ViewModelBase, IDisposable
   37: public BacktestStudioViewModel(
   59: public ObservableCollection<StrategyKernelDescriptor> Strategies { get; }
   60: public ObservableCollection<ParamRowViewModel> Parameters { get; }
   61: public ObservableCollection<RoundTripTrade> Trades { get; }
   62: public ObservableCollection<AxisRowViewModel> Axes { get; }
   63: public ObservableCollection<TrialRowViewModel> OptimizationTrials { get; }
   64: public ObservableCollection<WalkForwardRowViewModel> WalkForwardRows { get; }
   65: public IReadOnlyList<OptimizationCriterion> Criteria { get; }
   66: public IReadOnlyList<OptimizationMethod> Methods { get; }
   67: public IReadOnlyList<DataSourceKind> DataSources { get; } = Enum.GetValues<DataSourceKind>();
   68: public IReadOnlyList<BrokerKind> Brokers { get; } = Enum.GetValues<BrokerKind>();
   81: public BacktestReport? Report { get; private set; }
  133: public double[,]? SurfaceScores { get; private set; }
  134: public AxisRowViewModel? SurfaceXAxis { get; private set; }
  135: public AxisRowViewModel? SurfaceYAxis { get; private set; }
  137: public bool IsNotRunning => !IsRunning;
  138: public bool IsNotOptimizing => !IsOptimizing;
  139: public string CurrentBarText => $"{CurrentBar} / {BarCount}";
  142: public event EventHandler? OptimizationReady;
  145: public event EventHandler? ReportReady;
  148: public event EventHandler? ReplayFrameChanged;
  522: public void Dispose()
```

## src/linux/Tools/TradingTerminal.BacktestStudio/DataSourceKind.cs
```cs
    4: public enum DataSourceKind
```

## src/linux/Tools/TradingTerminal.BacktestStudio/LegacyKernelDescriptors.cs
```cs
   14: public static class LegacyKernelDescriptors
   16: public static IEnumerable<StrategyKernelDescriptor> From(IBacktestStrategyRegistry registry, ISet<string> excludeIds)
```

## src/linux/Tools/TradingTerminal.BacktestStudio/ParamRowViewModel.cs
```cs
    8: public sealed partial class ParamRowViewModel : ObservableObject
   10: public ParamRowViewModel(ParameterDescriptor descriptor)
   16: public ParameterDescriptor Descriptor { get; }
   17: public string Name => Descriptor.Name;
   18: public string Label => Descriptor.Label;
   23: public double Resolved => Descriptor.Clamp(Value);
```

## src/linux/Tools/TradingTerminal.BacktestStudio/ParquetMarketDataFeed.cs
```cs
   16: public sealed class ParquetMarketDataFeed : IMarketDataFeed
   23: public ParquetMarketDataFeed(InstrumentId instrument, string path, DateTime? fromUtc, DateTime? toUtc)
   31: public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
```

## src/linux/Tools/TradingTerminal.BacktestStudio/TrialRowViewModel.cs
```cs
    7: public sealed class TrialRowViewModel
    9: public TrialRowViewModel(OptimizationTrial trial)
   17: public double Score { get; }
   18: public double NetProfit { get; }
   19: public int TradeCount { get; }
   20: public string Parameters { get; }
```

## src/linux/Tools/TradingTerminal.BacktestStudio/WalkForwardRowViewModel.cs
```cs
    7: public sealed class WalkForwardRowViewModel
    9: public WalkForwardRowViewModel(WalkForwardFold fold)
   19: public string Window { get; }
   20: public double InSampleScore { get; }
   21: public double OutOfSampleScore { get; }
   22: public double OutOfSampleNetProfit { get; }
   23: public int OutOfSampleTrades { get; }
   24: public string Parameters { get; }
```
