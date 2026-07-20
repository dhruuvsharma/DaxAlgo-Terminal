# TradingTerminal.Core / Backtest — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Backtest/BacktestConfig.cs
```cs
    9: public enum BacktestDataSource
   37: public sealed record BacktestConfig(
```

## src/linux/Core/TradingTerminal.Core/Backtest/BacktestResult.cs
```cs
    8: public sealed record BacktestResult(
```

## src/linux/Core/TradingTerminal.Core/Backtest/BacktestStatistics.cs
```cs
   19: public sealed record BacktestStatistics(
```

## src/linux/Core/TradingTerminal.Core/Backtest/BacktestStrategyOption.cs
```cs
   14: public sealed record BacktestStrategyOption(
   21: public StrategyParameterSchema Schema { get; init; } = StrategyParameterSchema.Empty;
   24: public Func<Contract, StrategyParameters, IBacktestStrategy>? ParameterizedBuild { get; init; }
   32: public Func<Contract, IBacktestStrategy>? BacktestBuild { get; init; }
   36: public IBacktestStrategy CreateForBacktest(Contract contract) =>
   40: public bool HasParameters => !Schema.IsEmpty;
   48: public StrategyDataRequirement DataRequirement { get; init; } =
   57: public string? ResearchPaperUrl { get; init; }
   64: public IBacktestStrategy Create(Contract contract, StrategyParameters? parameters = null) =>
```

## src/linux/Core/TradingTerminal.Core/Backtest/EquityPoint.cs
```cs
    4: public sealed record EquityPoint(DateTime TimestampUtc, double Equity);
```

## src/linux/Core/TradingTerminal.Core/Backtest/Fast/FastBacktestRequest.cs
```cs
   13: public sealed record FastBacktestRequest(
```

## src/linux/Core/TradingTerminal.Core/Backtest/Fast/FastBacktestResult.cs
```cs
   11: public sealed record FastBacktestResult(
```

## src/linux/Core/TradingTerminal.Core/Backtest/Fast/IFastBacktestRunner.cs
```cs
   14: public interface IFastBacktestRunner
   16:     bool IsAvailable { get; }
   18:     Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default);
   21: public sealed class FastBacktestUnavailableException : Exception
   23: public FastBacktestUnavailableException(string message) : base(message) { }
   24: public FastBacktestUnavailableException(string message, Exception inner) : base(message, inner) { }
```

## src/linux/Core/TradingTerminal.Core/Backtest/FillRecord.cs
```cs
   10: public sealed record FillRecord(
```

## src/linux/Core/TradingTerminal.Core/Backtest/IBacktestSession.cs
```cs
   10: public interface IBacktestSession
   16:     Task<BacktestResult> RunAsync(
   17:     BacktestConfig config,
   18:     IBacktestStrategy strategy,
   19:     IRiskManager? risk = null,
   20:     CancellationToken ct = default);
```

## src/linux/Core/TradingTerminal.Core/Backtest/IBacktestStrategy.cs
```cs
   16: public interface IBacktestStrategy
   19:     Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct);
   22:     Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct);
   31:     Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
   32:     => Task.CompletedTask;
   42:     Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
   43:     => Task.CompletedTask;
   46:     Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct);
   49:     Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct);
```

## src/linux/Core/TradingTerminal.Core/Backtest/IParquetQueryService.cs
```cs
   16: public interface IParquetQueryService
   23:     IAsyncEnumerable<Tick> ReadTicksAsync(
   24:     string parquetGlob,
   25:     DateTime? fromUtc = null,
   26:     DateTime? toUtc = null,
   27:     CancellationToken ct = default);
   36:     Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
   37:     string parquetGlob,
   38:     TimeSpan interval,
   39:     DateTime? fromUtc = null,
   40:     DateTime? toUtc = null,
   41:     CancellationToken ct = default);
   50:     Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default);
   55: public sealed record OhlcvAggregate(
   66: public sealed record ParquetQueryResult(
```

## src/linux/Core/TradingTerminal.Core/Backtest/MonteCarlo.cs
```cs
   17: public static class MonteCarlo
   19: public sealed record Result(
   37: public static Result Run(
```

## src/linux/Core/TradingTerminal.Core/Backtest/Trade.cs
```cs
    9: public sealed record Trade(
```

## src/linux/Core/TradingTerminal.Core/Backtest/TransactionCostAnalysis.cs
```cs
   15: public static class TransactionCostAnalysis
   17: public sealed record Report(
   33: public sealed record HourBucket(int Hour, int Fills, double MeanSlippage, double MakerFraction);
   35: public static Report Compute(IReadOnlyList<FillRecord> fills)
```
