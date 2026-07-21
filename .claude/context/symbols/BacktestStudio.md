# TradingTerminal.BacktestStudio — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/TradingTerminal.BacktestStudio/AvaloniaUi/BacktestStudioAvaloniaWindow.axaml.cs
```cs
    9: public partial class BacktestStudioAvaloniaWindow : Window
   11: public BacktestStudioAvaloniaWindow() => InitializeComponent();
```

## src/windows/Tools/TradingTerminal.BacktestStudio/AxisRowViewModel.cs
```cs
    8: public sealed partial class AxisRowViewModel : ObservableObject
   10: public AxisRowViewModel(ParameterDescriptor descriptor)
   18: public ParameterDescriptor Descriptor { get; }
   19: public string Name => Descriptor.Name;
   20: public string Label => Descriptor.Label;
   27: public ParameterAxis ToAxis() => ParameterAxis.Range(Name, Min, Max, Step);
```

## src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioServiceCollectionExtensions.cs
```cs
   15: public static class BacktestStudioServiceCollectionExtensions
   17: public static IServiceCollection AddBacktestStudioSurface(this IServiceCollection services)
```

## src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioView.xaml.cs
```cs
   13: public partial class BacktestStudioView : UserControl
   19: public BacktestStudioView()
```

## src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioViewModel.cs
```cs
   32: public sealed partial class BacktestStudioViewModel : ViewModelBase, IDisposable
   47: public BacktestStudioViewModel(
  116: public ObservableCollection<StrategyKernelDescriptor> Strategies { get; }
  117: public ObservableCollection<ParamRowViewModel> Parameters { get; }
  118: public ObservableCollection<RoundTripTrade> Trades { get; }
  119: public ObservableCollection<AxisRowViewModel> Axes { get; }
  120: public ObservableCollection<TrialRowViewModel> OptimizationTrials { get; }
  121: public ObservableCollection<WalkForwardRowViewModel> WalkForwardRows { get; }
  122: public IReadOnlyList<OptimizationCriterion> Criteria { get; }
  123: public IReadOnlyList<OptimizationMethod> Methods { get; }
  124: public IReadOnlyList<DataSourceKind> DataSources { get; } = Enum.GetValues<DataSourceKind>();
  125: public IReadOnlyList<BrokerKind> Brokers { get; } = Enum.GetValues<BrokerKind>();
  140: public BacktestReport? Report { get; private set; }
  203: public double[,]? SurfaceScores { get; private set; }
  204: public AxisRowViewModel? SurfaceXAxis { get; private set; }
  205: public AxisRowViewModel? SurfaceYAxis { get; private set; }
  207: public bool IsBusy => IsRunning || IsOptimizing;
  208: public bool IsNotRunning => !IsBusy;
  209: public bool IsNotOptimizing => !IsBusy;
  210: public string CurrentBarText => $"{CurrentBar} / {BarCount}";
  211: public string ExecutionTarget => SelectedStrategy is { } strategy && SupportsWorker(strategy)
  216: public event EventHandler? OptimizationReady;
  219: public event EventHandler? ReportReady;
  222: public event EventHandler? ReplayFrameChanged;
  822: public void Dispose()
```

## src/windows/Tools/TradingTerminal.BacktestStudio/DataSourceKind.cs
```cs
    4: public enum DataSourceKind
```

## src/windows/Tools/TradingTerminal.BacktestStudio/LegacyKernelDescriptors.cs
```cs
   14: public static class LegacyKernelDescriptors
   16: public static IEnumerable<StrategyKernelDescriptor> From(IBacktestStrategyRegistry registry, ISet<string> excludeIds)
```

## src/windows/Tools/TradingTerminal.BacktestStudio/ParamRowViewModel.cs
```cs
    8: public sealed partial class ParamRowViewModel : ObservableObject
   10: public ParamRowViewModel(ParameterDescriptor descriptor)
   16: public ParameterDescriptor Descriptor { get; }
   17: public string Name => Descriptor.Name;
   18: public string Label => Descriptor.Label;
   23: public double Resolved => Descriptor.Clamp(Value);
```

## src/windows/Tools/TradingTerminal.BacktestStudio/ParquetMarketDataFeed.cs
```cs
   16: public sealed class ParquetMarketDataFeed : IMarketDataFeed
   23: public ParquetMarketDataFeed(InstrumentId instrument, string path, DateTime? fromUtc, DateTime? toUtc)
   31: public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
```

## src/windows/Tools/TradingTerminal.BacktestStudio/TrialRowViewModel.cs
```cs
    7: public sealed class TrialRowViewModel
    9: public TrialRowViewModel(OptimizationTrial trial)
   17: public double Score { get; }
   18: public double NetProfit { get; }
   19: public int TradeCount { get; }
   20: public string Parameters { get; }
```

## src/windows/Tools/TradingTerminal.BacktestStudio/WalkForwardRowViewModel.cs
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
