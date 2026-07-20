# TradingTerminal.Backtest.Engine — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Accounting/Portfolio.cs
```cs
   23: public Portfolio(double startingCash, IReadOnlyDictionary<InstrumentId, double> multipliers, IFeeModel feeModel)
   30: public double Cash { get; private set; }
   31: public double TotalFees { get; private set; }
   32: public IReadOnlyList<RoundTripTrade> Trades => _trades;
   37: public void OnFill(InstrumentId id, DateTime utc, OrderSide side, long qty, double price, LiquidityFlag liquidity)
   86: public void OnMark(InstrumentId id, double mark)
  106: public double Equity()
  114: public Position SnapshotOf(InstrumentId id)
  125: public IReadOnlyCollection<Position> OpenPositions() =>
  133: public Queue<Lot> Lots = new();
  134: public long NetPosition;
  135: public double Mark;
  136: public double RealizedPnl;
  138: public double AveragePrice
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/BacktestEngine.cs
```cs
   23: public sealed class BacktestEngine
   27: public BacktestEngine(IMarketDataFeed feed) => _feed = feed;
   29: public async Task<BacktestReport> RunAsync(RunSpec spec, IStrategyKernel kernel, CancellationToken ct = default)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Cost/FeeModels.cs
```cs
   10: public static IFeeModel From(CostSpec cost) => cost.Model switch
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/EngineOrderRouter.cs
```cs
   22: public EngineOrderRouter(SimulatedOrderBook book, Universe universe)
   29: public IObservable<OrderEvent> OrderEvents => _events;
   31: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
   34: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/IFillModel.cs
```cs
   27: public L1TouchFillModel(int slippageTicks)
   33: public bool TryFill(WorkingOrder o, Tick tick, double tickSize, out double fillPrice, out long fillQty)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/SimulatedOrderBook.cs
```cs
   21: public SimulatedOrderBook(SimClock clock, IFillModel fillModel, Func<InstrumentId, double> tickSizeOf)
   29: public event Action<InstrumentId, OrderEvent>? Event;
   31: public OrderResult Submit(OrderRequest request, InstrumentId instrument)
   47: public void Cancel(string clientOrderId)
   61: public void OnQuote(InstrumentId instrument, Tick tick)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/WorkingOrder.cs
```cs
   10: public required OrderRequest Request { get; init; }
   11: public required InstrumentId Instrument { get; init; }
   12: public required string BrokerOrderId { get; init; }
   14: public long FilledQuantity { get; set; }
   15: public double TotalFillValue { get; set; }
   16: public OrderState State { get; set; } = OrderState.Working;
   18: public double? AveragePrice => FilledQuantity == 0 ? null : TotalFillValue / FilledQuantity;
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/AsyncMerge.cs
```cs
   14: public static async IAsyncEnumerable<MarketEvent> ByEventTime(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/IMarketDataFeed.cs
```cs
   11: public interface IMarketDataFeed
   13:     IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, CancellationToken ct);
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/InMemoryMarketDataFeed.cs
```cs
   11: public sealed class InMemoryMarketDataFeed : IMarketDataFeed
   15: public InMemoryMarketDataFeed(IEnumerable<MarketEvent> events) =>
   18: public async IAsyncEnumerable<MarketEvent> StreamAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/StoreMarketDataFeed.cs
```cs
   15: public sealed class StoreMarketDataFeed : IMarketDataFeed
   19: public StoreMarketDataFeed(IMarketDataStore store) => _store = store;
   21: public IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, CancellationToken ct)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/SyntheticMarketDataFeed.cs
```cs
   12: public sealed class SyntheticMarketDataFeed : IMarketDataFeed
   20: public SyntheticMarketDataFeed(InstrumentId instrument, int count, int seed = 1, double startPrice = 100.0, double spread = 0.02)
   29: public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/SyntheticTapeFeed.cs
```cs
   28: public sealed class SyntheticTapeFeed : IMarketDataFeed
   40: public SyntheticTapeFeed(InstrumentId instrument, int steps, int seed = 1, double startPrice = 5_000.0, double tickSize = 0.25)
   49: public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Kernels/BacktestStrategyKernelAdapter.cs
```cs
   16: public sealed class BacktestStrategyKernelAdapter : IStrategyKernel
   22: public BacktestStrategyKernelAdapter(IBacktestStrategy inner) => _inner = inner;
   26: public BacktestStrategyKernelAdapter(Func<Contract, IBacktestStrategy> build) => _build = build;
   30: public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
   36: public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct) =>
   39: public Task OnTradeAsync(InstrumentId instrument, TradePrint trade, IStrategyContext ctx, CancellationToken ct) =>
   42: public Task OnDepthAsync(InstrumentId instrument, DepthSnapshot depth, IStrategyContext ctx, CancellationToken ct) =>
   45: public Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct) =>
   48: public Task OnEndAsync(IStrategyContext ctx, CancellationToken ct) =>
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Kernels/MeanReversionKernel.cs
```cs
   13: public sealed class MeanReversionKernel : IStrategyKernel
   22: public static StrategyKernelDescriptor Descriptor { get; } = new(
   35: public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
   44: public async Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Kernels/NativeKernels.cs
```cs
   10: public static class NativeKernels
   12: public static IReadOnlyList<StrategyKernelDescriptor> All { get; } = new[]
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/MarketEvent.cs
```cs
    6: public enum MarketEventKind
   21: public readonly record struct MarketEvent(
   30: public static MarketEvent OfQuote(InstrumentId id, Tick quote) =>
   33: public static MarketEvent OfTrade(InstrumentId id, TradePrint trade) =>
   36: public static MarketEvent OfDepth(InstrumentId id, DateTime tsUtc, DepthSnapshot depth) =>
   39: public static MarketEvent OfBar(InstrumentId id, OhlcvBar bar) =>
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Criteria.cs
```cs
   10: public static double Score(OptimizationCriterion criterion, BacktestReport report)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/GeneticOptimizer.cs
```cs
   15: public sealed class GeneticOptimizer
   20: public GeneticOptimizer(Func<IMarketDataFeed> feedFactory, Func<IStrategyKernel> kernelFactory)
   26: public async Task<OptimizationResult> RunAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/GpuUnavailableException.cs
```cs
    5: public sealed class GpuUnavailableException : Exception
    7: public GpuUnavailableException(string message) : base(message) { }
    8: public GpuUnavailableException(string message, Exception inner) : base(message, inner) { }
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/HybridGridOptimizer.cs
```cs
   12: public sealed class HybridGridOptimizer
   18: public HybridGridOptimizer(
   26: public bool WillUseGpu(OptimizationSpec spec) => _gpu.IsAvailable && ProcessGpuOptimizer.Supports(spec);
   28: public async Task<(OptimizationResult Result, bool UsedGpu)> RunAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/ProcessGpuOptimizer.cs
```cs
   20: public sealed class ProcessGpuOptimizer
   25: public ProcessGpuOptimizer(string exePath) => _exePath = exePath;
   27: public bool IsAvailable => File.Exists(_exePath);
   29: public static bool Supports(OptimizationSpec spec) =>
   35: public async Task<OptimizationResult> RunAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/GridOptimizer.cs
```cs
   14: public sealed class GridOptimizer
   19: public GridOptimizer(Func<IMarketDataFeed> feedFactory, Func<IStrategyKernel> kernelFactory)
   25: public async Task<OptimizationResult> RunAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/TrialRunner.cs
```cs
   11: public static async Task<OptimizationTrial> EvaluateAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/WalkForwardOptimizer.cs
```cs
   13: public sealed class WalkForwardOptimizer
   18: public WalkForwardOptimizer(IReadOnlyList<MarketEvent> events, Func<IStrategyKernel> kernelFactory)
   24: public async Task<WalkForwardResult> RunAsync(OptimizationSpec spec, int folds, CancellationToken ct = default)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Polyglot/PythonStrategyDescriptors.cs
```cs
   10: public static class PythonStrategyDescriptors
   12: public static StrategyKernelDescriptor For(
   17: public static IEnumerable<StrategyKernelDescriptor> Discover(string folder, string pythonExe = "python")
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Polyglot/PythonStrategyKernel.cs
```cs
   20: public sealed class PythonStrategyKernel : IStrategyKernel, IDisposable
   30: public PythonStrategyKernel(string pythonExe, string scriptPath)
   36: public async Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
   60: public async Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
   74: public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
  111: public void Dispose()
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/SimClock.cs
```cs
   10: public sealed class SimClock : IClock
   12: public DateTime UtcNow { get; private set; } = DateTime.UnixEpoch;
   14: public void SetTo(DateTime utc) => UtcNow = utc;
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Stats/ReportBuilder.cs
```cs
   17: public static BacktestReport Build(
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/Stats/VisualRecorder.cs
```cs
   22: public VisualRecorder(InstrumentId instrument, TimeSpan interval)
   28: public void OnMid(InstrumentId instrument, DateTime ts, double mid)
   51: public VisualTimeline Build(IReadOnlyList<RoundTripTrade> trades)
```

## src/windows/Backtest/TradingTerminal.Backtest.Engine/StrategyContext.cs
```cs
   13: public StrategyContext(IClock clock, IOrderRouter router, IPortfolioView portfolio, Universe universe, StrategyParameters parameters)
   22: public IClock Clock { get; }
   23: public IOrderRouter Router { get; }
   24: public IPortfolioView Portfolio { get; }
   25: public Universe Universe { get; }
   26: public StrategyParameters Parameters { get; }
   35: public PortfolioView(Portfolio portfolio) => _portfolio = portfolio;
   37: public double Cash => _portfolio.Cash;
   38: public double Equity => _portfolio.Equity();
   39: public Position PositionOf(InstrumentId instrument) => _portfolio.SnapshotOf(instrument);
   40: public IReadOnlyCollection<Position> OpenPositions => _portfolio.OpenPositions();
```
