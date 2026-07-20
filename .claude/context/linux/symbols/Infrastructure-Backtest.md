# TradingTerminal.Infrastructure / Backtest — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/BacktestOrderRouter.cs
```cs
   19: public sealed class BacktestOrderRouter : IOrderRouter
   26: public BacktestOrderRouter(SimulatedOrderBook book, IRiskManager? risk = null)
   40: public IObservable<OrderEvent> OrderEvents => _book.Events.Merge(_localEvents);
   42: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
   67: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/BacktestSession.cs
```cs
   24: public sealed class BacktestSession : IBacktestSession
   30: public BacktestSession() : this(store: null) { }
   35: public BacktestSession(IMarketDataStore? store)
   40: public Task<BacktestResult> RunAsync(
   45: public async Task<BacktestResult> RunAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/BacktestStrategyCatalog.cs
```cs
   21: public static class BacktestStrategyCatalog
   28: public static IServiceCollection AddBacktestStrategyCatalog(this IServiceCollection services)
   36: public static IReadOnlyList<BacktestStrategyOption> All { get; } = new[]
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Fast/FastBacktestServiceCollectionExtensions.cs
```cs
   18: public static class FastBacktestServiceCollectionExtensions
   20: public static IServiceCollection AddFastBacktestRunner(this IServiceCollection services)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Fast/NullFastBacktestRunner.cs
```cs
   11: public sealed class NullFastBacktestRunner : IFastBacktestRunner
   15: public NullFastBacktestRunner(string reason)
   20: public bool IsAvailable => false;
   22: public Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default) =>
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Fast/ProcessFastBacktestRunner.cs
```cs
   25: public sealed class ProcessFastBacktestRunner : IFastBacktestRunner
   30: public ProcessFastBacktestRunner(ILogger<ProcessFastBacktestRunner> logger, string exePath)
   36: public bool IsAvailable => File.Exists(_exePath);
   38: public async Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/IBacktestStrategyRegistry.cs
```cs
   13: public interface IBacktestStrategyRegistry
   15:     IReadOnlyList<BacktestStrategyOption> All { get; }
   18:     BacktestStrategyOption? Find(string id);
   21:     void Register(BacktestStrategyOption option);
   24:     bool Remove(string id);
   27:     event EventHandler? Changed;
   32: public BacktestStrategyRegistry(IEnumerable<BacktestStrategyOption> options)
   41: public IReadOnlyList<BacktestStrategyOption> All
   46: public BacktestStrategyOption? Find(string id)
   51: public void Register(BacktestStrategyOption option)
   58: public bool Remove(string id)
   66: public event EventHandler? Changed;
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/L1FillModel.cs
```cs
   11: public interface IFillModel
   13:     bool TryFill(PendingOrder order, Tick tick, out double fillPrice, out long fillQty);
   23: public sealed class L1FillModel : IFillModel
   28: public L1FillModel(double tickSize, int slippageTicks)
   36: public bool TryFill(PendingOrder o, Tick tick, out double fillPrice, out long fillQty)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/PendingOrder.cs
```cs
    6: public sealed class PendingOrder
    8: public required OrderRequest Request { get; init; }
    9: public required string BrokerOrderId { get; init; }
   10: public long FilledQuantity { get; set; }
   11: public double TotalFillValue { get; set; }
   12: public OrderState State { get; set; } = OrderState.Working;
   14: public double? AveragePrice =>
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/BacktestTickSource.cs
```cs
   16: public static BacktestEvent FromQuote(Tick q) => new(q.TimestampUtc, q, null);
   17: public static BacktestEvent FromTrade(TradePrint t) => new(t.EventTimeUtc, null, t);
   31: public static IAsyncEnumerable<BacktestEvent> Resolve(BacktestConfig config, IMarketDataStore? store, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/CsvTickReader.cs
```cs
   15: public static class CsvTickReader
   17: public static async IAsyncEnumerable<Tick> ReadAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/CsvTradeReader.cs
```cs
   15: public static class CsvTradeReader
   17: public static async IAsyncEnumerable<TradePrint> ReadAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/DuckDbParquetQueryService.cs
```cs
   23: public sealed class DuckDbParquetQueryService : IParquetQueryService
   28: public DuckDbParquetQueryService(ILogger<DuckDbParquetQueryService> logger) => _logger = logger;
   30: public async IAsyncEnumerable<Tick> ReadTicksAsync(
   62: public async Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
  113: public async Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTickReader.cs
```cs
   14: public static class ParquetTickReader
   16: public static async IAsyncEnumerable<Tick> ReadAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTickWriter.cs
```cs
   13: public sealed class ParquetTickWriter : IAsyncDisposable
   20: public ParquetTickWriter(string path, int rowGroupSize = 50_000)
   31: public async ValueTask WriteAsync(Tick tick, CancellationToken ct = default)
   46: public async Task FlushAsync(CancellationToken ct = default)
   66: public async ValueTask DisposeAsync()
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTradeReader.cs
```cs
   15: public static class ParquetTradeReader
   17: public static async IAsyncEnumerable<TradePrint> ReadAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTradeWriter.cs
```cs
   12: public sealed class ParquetTradeWriter : IAsyncDisposable
   19: public ParquetTradeWriter(string path, int rowGroupSize = 50_000)
   30: public async ValueTask WriteAsync(TradePrint trade, CancellationToken ct = default)
   44: public async Task FlushAsync(CancellationToken ct = default)
   58: public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/TickRecord.cs
```cs
   11: public long TimestampMicros { get; set; }
   12: public double Bid { get; set; }
   13: public double Ask { get; set; }
   14: public long BidSize { get; set; }
   15: public long AskSize { get; set; }
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/TradeRecord.cs
```cs
   12: public long TimestampMicros { get; set; }
   13: public double Price { get; set; }
   14: public long Size { get; set; }
   15: public int Aggressor { get; set; }
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/UnsupportedParquetQueryService.cs
```cs
   13: public sealed class UnsupportedParquetQueryService : IParquetQueryService
   18: public IAsyncEnumerable<Tick> ReadTicksAsync(
   22: public Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
   26: public Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/SimulatedClock.cs
```cs
   10: public sealed class SimulatedClock : IClock
   14: public DateTime UtcNow => _now;
   17: public void SetTo(DateTime utc)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/SimulatedOrderBook.cs
```cs
   15: public sealed class SimulatedOrderBook
   23: public SimulatedOrderBook(IClock clock, IFillModel fillModel)
   29: public IObservable<OrderEvent> Events => _events.AsObservable();
   31: public OrderResult Submit(OrderRequest request)
   50: public void Cancel(string clientOrderId)
   63: public void OnTick(Tick tick)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/StatisticsCalculator.cs
```cs
   15: public static class StatisticsCalculator
   19: public static BacktestStatistics Calculate(BacktestResult result)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ApexScalperStrategy.cs
```cs
   15: public sealed record ApexLiveCandle(
   70: public sealed class ApexScalperStrategy : IBacktestStrategy
   73: public const string SigDelta = "DELTA";
   74: public const string SigVpin = "VPIN";
   75: public const string SigFootprint = "FOOTPRINT";
   76: public const string SigTapeSpeed = "TAPE_SPEED";
   77: public const string SigKyle = "KYLE";
   78: public const string SigInitiative = "INITIATIVE";
   79: public const string SigControl = "CONTROL";
   80: public const string SigWedge = "WEDGE";
   81: public const string SigValue = "VALUE";
   82: public const string SigCvd = "CVD";
   83: public const string SigObi = "OBI";
   84: public const string SigPredNode = "PRED_NODE";
   89: public static readonly string[] SignalNames =
   96: public ApexV2Options Options { get; }
   97: public TimeSpan CandleInterval { get; }
  100: public double InstrumentTick { get; }
  211: public bool PaperTradingEnabled { get; set; } = true;
  218: public IReadOnlyList<ApexTradeRecord> Trades => _trades;
  221: public double OpenEntryPrice => _entryPrice;
  222: public double OpenStopPrice => _stopPrice;
  223: public double OpenTargetPrice => _targetPrice;
  224: public double Balance => _balance;
  231: public ApexSnapshotV2? Latest { get; private set; }
  234: public IReadOnlyList<ApexSnapshotV2> History
  247: public IReadOnlyList<FootprintBar> FootprintBars
  263: public ApexLiveCandle? LiveCandle
  278: public ApexScalperStrategy(Contract contract, ApexV2Options? options = null, TimeSpan? candleInterval = null, double instrumentTick = 0.25)
  313: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
  320: public void SeedFromBars(IReadOnlyList<Bar> bars)
  378: public Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
  392: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  405: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  587: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
  593: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 1763: public static SignalResult Invalid(string name, DateTime now) => new(name, 0, 0, 0, false, now);
 1764: public static SignalResult From(string name, double score, double confidence, DateTime now)
 1787: public static LineTriple Empty => new(ApexLineFit.Empty, ApexLineFit.Empty, ApexLineFit.Empty, 0);
 1788: public bool Valid => Buy.FittedEndpoint > 0 && Sell.FittedEndpoint > 0;
 1793: public DateTime OpenTime;
 1794: public double Open, High, Low, Close;
 1795: public long BuyVolume, SellVolume;
 1803: public void Add(FootprintPrint p)
 1809: public double ArrivalRate(DateTime now, double windowSec)
 1818: public double UpTickFraction(DateTime now, double windowSec)
 1841: public RingBuffer(int capacity) { _capacity = Math.Max(1, capacity); _q = new Queue<T>(_capacity); }
 1842: public int Count => _q.Count;
 1843: public int Capacity => _capacity;
 1844: public void Push(T v) { _q.Enqueue(v); while (_q.Count > _capacity) _q.Dequeue(); }
 1845: public T[] ToArray() => _q.ToArray();
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/BuyAndHoldStrategy.cs
```cs
   13: public sealed class BuyAndHoldStrategy : IBacktestStrategy
   20: public BuyAndHoldStrategy(Contract contract, long quantity = 1)
   26: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   28: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   41: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   43: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/DonchianBreakoutStrategy.cs
```cs
   15: public sealed class DonchianBreakoutStrategy : IBacktestStrategy
   17: public int Lookback { get; }
   18: public double TrailingStopDistance { get; }
   19: public long Quantity { get; }
   29: public DonchianBreakoutStrategy(
   43: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   45: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   93: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   95: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/FilteredOrderFlowStrategy.cs
```cs
   34: public sealed class FilteredOrderFlowStrategy : IBacktestStrategy
   37: public double WindowSeconds { get; }
   41: public long MinTradeSize { get; }
   44: public int StrongRegime { get; }
   48: public double HoldSeconds { get; }
   50: public long Quantity { get; }
   67: public FilteredOrderFlowStrategy(
   85: public double FilteredObi { get; private set; }
   87: public double UnfilteredObi { get; private set; }
   89: public int FilteredRegime { get; private set; }
   91: public long FilteredTradesInWindow => _buyFilt + _sellFilt;
   93: public long UnfilteredTradesInWindow => _buyAll + _sellAll;
   95: public long Position => _position;
   99: public event Action? Updated;
  101: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
  103: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  110: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  188: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  190: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/IReproStrategyRegistrar.cs
```cs
   18: public interface IReproStrategyRegistrar
   23:     Task<ReproRegistration> RegisterAsync(ReproResult result, CancellationToken ct = default);
   27: public sealed record ReproRegistration(
   34: public static ReproRegistration Failed(string reason, ReproSignalManifest? manifest = null) =>
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ImbalanceHeatFrontStrategy.cs
```cs
   31: public sealed class ImbalanceHeatFrontStrategy : IBacktestStrategy
   33: public enum RidgeMode { Momentum, MeanReversion }
   35: public int NumLevels { get; }
   36: public int NumSlices { get; }
   37: public double RidgeThreshold { get; }
   38: public int RidgeWidth { get; }
   39: public int ConfirmationSlices { get; }
   40: public RidgeMode Mode { get; }
   41: public long Quantity { get; }
   42: public double StopLossPips { get; }
   43: public double TakeProfitPips { get; }
   58: public ImbalanceHeatFrontStrategy(
   94: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   96: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  110: public async Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
  126: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  128: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/IndexKScoreSurfaceStrategy.cs
```cs
   21: public sealed class IndexKScoreSurfaceStrategy : IBacktestStrategy
   23: public TimeSpan BarInterval { get; }
   24: public double EntryThreshold { get; }
   25: public double ExitThreshold { get; }
   26: public long Quantity { get; }
   27: public IndexKScoreParameters Parameters { get; }
   37: public IndexKScoreSurfaceStrategy(
   60: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   62: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   95: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   97: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/MeanReversionStrategy.cs
```cs
   17: public sealed class MeanReversionStrategy : IBacktestStrategy
   19: public int LookbackTicks { get; }
   20: public double EntryThreshold { get; }
   21: public double StopThreshold { get; }
   22: public long Quantity { get; }
   31: public MeanReversionStrategy(
   46: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   48: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   98: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  100: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/OrderFlowCubeStrategy.cs
```cs
   35: public sealed class OrderFlowCubeStrategy : IBacktestStrategy
   37: public int WindowTrades { get; }
   38: public int BaselineTrades { get; }
   39: public double CvdImbalanceThreshold { get; }
   40: public double AggressorBuyThreshold { get; }
   41: public double SizeRatioThreshold { get; }
   42: public int HoldTrades { get; }
   43: public long Quantity { get; }
   57: public OrderFlowCubeStrategy(
   79: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   82: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   84: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  161: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  163: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/OrderFlowSurfaceSpikeStrategy.cs
```cs
   28: public sealed class OrderFlowSurfaceSpikeStrategy : IBacktestStrategy
   30: public int TicksPerSlice { get; }
   31: public int NumSlices { get; }
   32: public double PriceBinSize { get; }
   33: public double SpikeThreshold { get; }
   34: public long Quantity { get; }
   35: public double StopLossPips { get; }
   36: public double TakeProfitPips { get; }
   37: public int ConfirmationTicks { get; }
   50: public OrderFlowSurfaceSpikeStrategy(
   81: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   84: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   86: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  151: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  153: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproStrategyRegistrar.cs
```cs
   18: public ReproStrategyRegistrar(
   30: public async Task<ReproRegistration> RegisterAsync(ReproResult result, CancellationToken ct = default)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproducedSignalBacktestStrategy.cs
```cs
   19: public sealed class ReproducedSignalBacktestStrategy : IBacktestStrategy
   27: public ReproducedSignalBacktestStrategy(Contract contract, ReproSignalManifest manifest, InstrumentId instrument, long qty = 1)
   37: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   39: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   51: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   53: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproducedSignalStrategyKernel.cs
```cs
   29: public sealed class ReproducedSignalStrategyKernel : IStrategyKernel
   38: public ReproducedSignalStrategyKernel(ReproSignalManifest manifest, long qty = 1)
   44: public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
   65: public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct) =>
   68: public Task OnBarAsync(InstrumentId instrument, OhlcvBar bar, IStrategyContext ctx, CancellationToken ct) =>
  110: public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproducedStrategyFactory.cs
```cs
   20: public static class ReproducedStrategyFactory
   24: public const string IdPrefix = "repro-";
   28: public static string IdFor(ReproSignalManifest manifest)
   42: public static StrategyKernelDescriptor ToKernelDescriptor(ReproSignalManifest manifest, long qty = 1) =>
   60: public static BacktestStrategyOption ToBacktestOption(ReproSignalManifest manifest, long qty = 1)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/TradeLedger.cs
```cs
   19: public TradeLedger(double multiplier, double startingCash, IFeeModel? feeModel = null)
   26: public double Cash { get; private set; }
   27: public long NetPosition { get; private set; }
   28: public double TotalFees { get; private set; }
   29: public IReadOnlyList<Trade> Trades => _trades;
   31: public void OnFill(DateTime utc, OrderSide side, long qty, double price, LiquidityFlag liquidity = LiquidityFlag.Taker)
   68: public double Equity(double mid) => Cash + NetPosition * mid * _multiplier;
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Backtest/WalkForwardGridBuilders.cs
```cs
   15: public static class WalkForwardGridBuilders
   17: public static IReadOnlyList<(string Label, Func<Contract, IBacktestStrategy> Builder)> Build(
   33: public static IReadOnlyList<(string, Func<Contract, IBacktestStrategy>)> MeanReversion(
   48: public static IReadOnlyList<(string, Func<Contract, IBacktestStrategy>)> Donchian(
```
