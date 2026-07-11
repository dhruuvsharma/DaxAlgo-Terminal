# TradingTerminal.Infrastructure / Backtest — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/BacktestOrderRouter.cs
```cs
   19: public sealed class BacktestOrderRouter : IOrderRouter
   26: public BacktestOrderRouter(SimulatedOrderBook book, IRiskManager? risk = null)
   40: public IObservable<OrderEvent> OrderEvents => _book.Events.Merge(_localEvents);
   42: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
   67: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/BacktestSession.cs
```cs
   24: public sealed class BacktestSession : IBacktestSession
   30: public BacktestSession() : this(store: null) { }
   35: public BacktestSession(IMarketDataStore? store)
   40: public Task<BacktestResult> RunAsync(
   45: public async Task<BacktestResult> RunAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/BacktestStrategyCatalog.cs
```cs
   22: public static class BacktestStrategyCatalog
   29: public static IServiceCollection AddBacktestStrategyCatalog(this IServiceCollection services)
   37: public static IReadOnlyList<BacktestStrategyOption> All { get; } = new[]
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Fast/FastBacktestServiceCollectionExtensions.cs
```cs
   18: public static class FastBacktestServiceCollectionExtensions
   20: public static IServiceCollection AddFastBacktestRunner(this IServiceCollection services)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Fast/NullFastBacktestRunner.cs
```cs
   11: public sealed class NullFastBacktestRunner : IFastBacktestRunner
   15: public NullFastBacktestRunner(string reason)
   20: public bool IsAvailable => false;
   22: public Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default) =>
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Fast/ProcessFastBacktestRunner.cs
```cs
   25: public sealed class ProcessFastBacktestRunner : IFastBacktestRunner
   30: public ProcessFastBacktestRunner(ILogger<ProcessFastBacktestRunner> logger, string exePath)
   36: public bool IsAvailable => File.Exists(_exePath);
   38: public async Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/IBacktestStrategyRegistry.cs
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

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/L1FillModel.cs
```cs
   11: public interface IFillModel
   13:     bool TryFill(PendingOrder order, Tick tick, out double fillPrice, out long fillQty);
   23: public sealed class L1FillModel : IFillModel
   28: public L1FillModel(double tickSize, int slippageTicks)
   36: public bool TryFill(PendingOrder o, Tick tick, out double fillPrice, out long fillQty)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/PendingOrder.cs
```cs
    6: public sealed class PendingOrder
    8: public required OrderRequest Request { get; init; }
    9: public required string BrokerOrderId { get; init; }
   10: public long FilledQuantity { get; set; }
   11: public double TotalFillValue { get; set; }
   12: public OrderState State { get; set; } = OrderState.Working;
   14: public double? AveragePrice =>
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/BacktestTickSource.cs
```cs
   16: public static BacktestEvent FromQuote(Tick q) => new(q.TimestampUtc, q, null);
   17: public static BacktestEvent FromTrade(TradePrint t) => new(t.EventTimeUtc, null, t);
   31: public static IAsyncEnumerable<BacktestEvent> Resolve(BacktestConfig config, IMarketDataStore? store, CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/CsvTickReader.cs
```cs
   15: public static class CsvTickReader
   17: public static async IAsyncEnumerable<Tick> ReadAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/CsvTradeReader.cs
```cs
   15: public static class CsvTradeReader
   17: public static async IAsyncEnumerable<TradePrint> ReadAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/DuckDbParquetQueryService.cs
```cs
   23: public sealed class DuckDbParquetQueryService : IParquetQueryService
   28: public DuckDbParquetQueryService(ILogger<DuckDbParquetQueryService> logger) => _logger = logger;
   30: public async IAsyncEnumerable<Tick> ReadTicksAsync(
   62: public async Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
  113: public async Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTickReader.cs
```cs
   14: public static class ParquetTickReader
   16: public static async IAsyncEnumerable<Tick> ReadAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTickWriter.cs
```cs
   13: public sealed class ParquetTickWriter : IAsyncDisposable
   20: public ParquetTickWriter(string path, int rowGroupSize = 50_000)
   31: public async ValueTask WriteAsync(Tick tick, CancellationToken ct = default)
   46: public async Task FlushAsync(CancellationToken ct = default)
   66: public async ValueTask DisposeAsync()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTradeReader.cs
```cs
   15: public static class ParquetTradeReader
   17: public static async IAsyncEnumerable<TradePrint> ReadAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/ParquetTradeWriter.cs
```cs
   12: public sealed class ParquetTradeWriter : IAsyncDisposable
   19: public ParquetTradeWriter(string path, int rowGroupSize = 50_000)
   30: public async ValueTask WriteAsync(TradePrint trade, CancellationToken ct = default)
   44: public async Task FlushAsync(CancellationToken ct = default)
   58: public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/TickRecord.cs
```cs
   11: public long TimestampMicros { get; set; }
   12: public double Bid { get; set; }
   13: public double Ask { get; set; }
   14: public long BidSize { get; set; }
   15: public long AskSize { get; set; }
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/TradeRecord.cs
```cs
   12: public long TimestampMicros { get; set; }
   13: public double Price { get; set; }
   14: public long Size { get; set; }
   15: public int Aggressor { get; set; }
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Persistence/UnsupportedParquetQueryService.cs
```cs
   13: public sealed class UnsupportedParquetQueryService : IParquetQueryService
   18: public IAsyncEnumerable<Tick> ReadTicksAsync(
   22: public Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
   26: public Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/SimulatedClock.cs
```cs
   10: public sealed class SimulatedClock : IClock
   14: public DateTime UtcNow => _now;
   17: public void SetTo(DateTime utc)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/SimulatedOrderBook.cs
```cs
   15: public sealed class SimulatedOrderBook
   23: public SimulatedOrderBook(IClock clock, IFillModel fillModel)
   29: public IObservable<OrderEvent> Events => _events.AsObservable();
   31: public OrderResult Submit(OrderRequest request)
   50: public void Cancel(string clientOrderId)
   63: public void OnTick(Tick tick)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/StatisticsCalculator.cs
```cs
   15: public static class StatisticsCalculator
   19: public static BacktestStatistics Calculate(BacktestResult result)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/BuyAndHoldStrategy.cs
```cs
   13: public sealed class BuyAndHoldStrategy : IBacktestStrategy
   20: public BuyAndHoldStrategy(Contract contract, long quantity = 1)
   26: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   28: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   41: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   43: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/DonchianBreakoutStrategy.cs
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

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/IReproStrategyRegistrar.cs
```cs
   18: public interface IReproStrategyRegistrar
   23:     Task<ReproRegistration> RegisterAsync(ReproResult result, CancellationToken ct = default);
   27: public sealed record ReproRegistration(
   34: public static ReproRegistration Failed(string reason, ReproSignalManifest? manifest = null) =>
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/MeanReversionStrategy.cs
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

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproStrategyRegistrar.cs
```cs
   18: public ReproStrategyRegistrar(
   30: public async Task<ReproRegistration> RegisterAsync(ReproResult result, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproducedSignalBacktestStrategy.cs
```cs
   19: public sealed class ReproducedSignalBacktestStrategy : IBacktestStrategy
   27: public ReproducedSignalBacktestStrategy(Contract contract, ReproSignalManifest manifest, InstrumentId instrument, long qty = 1)
   37: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   39: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   51: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   53: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproducedSignalStrategyKernel.cs
```cs
   29: public sealed class ReproducedSignalStrategyKernel : IStrategyKernel
   38: public ReproducedSignalStrategyKernel(ReproSignalManifest manifest, long qty = 1)
   44: public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
   65: public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct) =>
   68: public Task OnBarAsync(InstrumentId instrument, OhlcvBar bar, IStrategyContext ctx, CancellationToken ct) =>
  110: public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/Strategies/ReproducedStrategyFactory.cs
```cs
   20: public static class ReproducedStrategyFactory
   24: public const string IdPrefix = "repro-";
   28: public static string IdFor(ReproSignalManifest manifest)
   42: public static StrategyKernelDescriptor ToKernelDescriptor(ReproSignalManifest manifest, long qty = 1) =>
   60: public static BacktestStrategyOption ToBacktestOption(ReproSignalManifest manifest, long qty = 1)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/TradeLedger.cs
```cs
   19: public TradeLedger(double multiplier, double startingCash, IFeeModel? feeModel = null)
   26: public double Cash { get; private set; }
   27: public long NetPosition { get; private set; }
   28: public double TotalFees { get; private set; }
   29: public IReadOnlyList<Trade> Trades => _trades;
   31: public void OnFill(DateTime utc, OrderSide side, long qty, double price, LiquidityFlag liquidity = LiquidityFlag.Taker)
   68: public double Equity(double mid) => Cash + NetPosition * mid * _multiplier;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/WalkForwardGridBuilders.cs
```cs
   17: public static class WalkForwardGridBuilders
   21: public static IReadOnlyList<(string Label, Func<Contract, IBacktestStrategy> Builder)> For(
```
