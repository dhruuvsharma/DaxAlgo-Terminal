# TradingTerminal.Core / Backtesting — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Backtesting/BacktestReport.cs
```cs
    9: public sealed record EquitySample(DateTime TimestampUtc, double Equity, double Balance, double Drawdown);
   16: public sealed record RoundTripTrade(
   29: public double NetPnl => GrossPnl - Fees;
   30: public TimeSpan HoldingTime => ExitUtc - EntryUtc;
   31: public bool IsWin => NetPnl > 0;
   36: public sealed record InstrumentReport(InstrumentId Instrument, double NetPnl, int TradeCount, double WinRate);
   39: public sealed record RunSummary(
   47: public double NetProfit => EndingEquity - StartingCash;
   48: public double TotalReturn => StartingCash == 0 ? 0 : NetProfit / StartingCash;
   57: public sealed class MetricSet
   61: public MetricSet(IReadOnlyDictionary<string, double> metrics) => _metrics = metrics;
   63: public static MetricSet Empty { get; } = new(new Dictionary<string, double>());
   65: public double this[string key] => _metrics[key];
   66: public bool Has(string key) => _metrics.ContainsKey(key);
   67: public double GetOr(string key, double fallback = double.NaN) =>
   69: public IReadOnlyDictionary<string, double> All => _metrics;
   72: public double Sharpe => GetOr(Keys.Sharpe);
   73: public double Sortino => GetOr(Keys.Sortino);
   74: public double Calmar => GetOr(Keys.Calmar);
   75: public double MaxDrawdown => GetOr(Keys.MaxDrawdown);
   76: public double ProfitFactor => GetOr(Keys.ProfitFactor);
   77: public double WinRate => GetOr(Keys.WinRate);
   78: public double Expectancy => GetOr(Keys.Expectancy);
   81: public static class Keys
   83: public const string Sharpe = "sharpe";
   84: public const string Sortino = "sortino";
   85: public const string Calmar = "calmar";
   86: public const string Omega = "omega";
   87: public const string MaxDrawdown = "max_drawdown";
   88: public const string UlcerIndex = "ulcer_index";
   89: public const string ProfitFactor = "profit_factor";
   90: public const string WinRate = "win_rate";
   91: public const string Expectancy = "expectancy";
   92: public const string RecoveryFactor = "recovery_factor";
   93: public const string DownsideDeviation = "downside_deviation";
   94: public const string MaxConsecutiveLosses = "max_consecutive_losses";
   95: public const string AvgHoldingSeconds = "avg_holding_seconds";
  105: public sealed record BacktestReport(
```

## src/linux/Core/TradingTerminal.Core/Backtesting/IStrategyContext.cs
```cs
   15: public interface IStrategyContext
   18:     IClock Clock { get; }
   21:     IOrderRouter Router { get; }
   24:     IPortfolioView Portfolio { get; }
   27:     Universe Universe { get; }
   30:     StrategyParameters Parameters { get; }
```

## src/linux/Core/TradingTerminal.Core/Backtesting/IStrategyKernel.cs
```cs
   17: public interface IStrategyKernel
   20:     Task OnStartAsync(IStrategyContext ctx, CancellationToken ct);
   23:     Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
   24:     => Task.CompletedTask;
   27:     Task OnTradeAsync(InstrumentId instrument, TradePrint trade, IStrategyContext ctx, CancellationToken ct)
   28:     => Task.CompletedTask;
   31:     Task OnDepthAsync(InstrumentId instrument, DepthSnapshot depth, IStrategyContext ctx, CancellationToken ct)
   32:     => Task.CompletedTask;
   35:     Task OnBarAsync(InstrumentId instrument, OhlcvBar bar, IStrategyContext ctx, CancellationToken ct)
   36:     => Task.CompletedTask;
   39:     Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct)
   40:     => Task.CompletedTask;
   43:     Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
   44:     => Task.CompletedTask;
```

## src/linux/Core/TradingTerminal.Core/Backtesting/Optimization.cs
```cs
    9: public sealed record ParameterAxis(string Name, IReadOnlyList<double> Values)
   11: public static ParameterAxis Of(string name, params double[] values) => new(name, values);
   13: public static ParameterAxis Range(string name, double min, double max, double step)
   24: public enum OptimizationCriterion
   37: public enum OptimizationMethod
   52: public sealed record OptimizationSpec(
   61: public sealed record OptimizationTrial(
   68: public sealed record OptimizationResult(
   73: public int Evaluations => Trials.Count;
   78: public sealed record GeneticOptions(
   91: public sealed record WalkForwardFold(
  105: public sealed record WalkForwardResult(IReadOnlyList<WalkForwardFold> Folds)
  107: public double AvgInSampleScore => Folds.Count == 0 ? 0 : Folds.Average(f => f.InSampleScore);
  108: public double AvgOutOfSampleScore => Folds.Count == 0 ? 0 : Folds.Average(f => f.OutOfSampleScore);
  109: public double TotalOutOfSampleNetProfit => Folds.Sum(f => f.OutOfSampleNetProfit);
  110: public double Efficiency => AvgInSampleScore == 0 ? 0 : AvgOutOfSampleScore / AvgInSampleScore;
```

## src/linux/Core/TradingTerminal.Core/Backtesting/Portfolio.cs
```cs
   11: public readonly record struct Position(
   18: public bool IsFlat => Quantity == 0;
   19: public bool IsLong => Quantity > 0;
   20: public bool IsShort => Quantity < 0;
   22: public static Position Flat(InstrumentId instrument) => new(instrument, 0, 0, 0, 0);
   31: public interface IPortfolioView
   34:     double Cash { get; }
   37:     double Equity { get; }
   40:     Position PositionOf(InstrumentId instrument);
   43:     IReadOnlyCollection<Position> OpenPositions { get; }
```

## src/linux/Core/TradingTerminal.Core/Backtesting/RunSpec.cs
```cs
    4: public enum BacktestDataSource
   17: public enum ModelingMode
   33: public enum FillModelKind
   46: public enum CostModelKind
   54: public enum VisualRecording
   68: public sealed record DataSpec(
   78: public sealed record ExecutionSpec(
   85: public sealed record CostSpec(
  101: public sealed record RunSpec(
  111: public ExecutionSpec ExecutionOrDefault => Execution ?? new ExecutionSpec();
  112: public CostSpec CostOrDefault => Cost ?? new CostSpec();
  113: public StrategyParameters ParametersOrEmpty => Parameters ?? StrategyParameters.Empty;
```

## src/linux/Core/TradingTerminal.Core/Backtesting/StrategyKernelRegistry.cs
```cs
   10: public sealed record StrategyKernelDescriptor(
   23: public string? ResearchPaperUrl { get; init; }
   28: public interface IStrategyKernelRegistry
   30:     IReadOnlyList<StrategyKernelDescriptor> All { get; }
   31:     StrategyKernelDescriptor? Find(string id);
   32:     IStrategyKernel Create(string id);
   33:     bool TryCreate(string id, out IStrategyKernel kernel);
   38: public sealed class StrategyKernelRegistry : IStrategyKernelRegistry
   42: public StrategyKernelRegistry(IEnumerable<StrategyKernelDescriptor> descriptors) =>
   45: public IReadOnlyList<StrategyKernelDescriptor> All => _byId.Values.ToList();
   47: public StrategyKernelDescriptor? Find(string id) =>
   50: public IStrategyKernel Create(string id) =>
   53: public bool TryCreate(string id, out IStrategyKernel kernel)
```

## src/linux/Core/TradingTerminal.Core/Backtesting/StrategyParameterSchema.cs
```cs
    4: public enum ParameterKind
   19: public sealed record ParameterDescriptor(
   30: public double Clamp(double value)
   45: public sealed record StrategyParameterSchema(IReadOnlyList<ParameterDescriptor> Parameters)
   47: public static StrategyParameterSchema Empty { get; } = new(Array.Empty<ParameterDescriptor>());
   49: public ParameterDescriptor? Find(string name) => Parameters.FirstOrDefault(p => p.Name == name);
   51: public StrategyParameters Defaults() =>
   55: public StrategyParameters Resolve(IReadOnlyDictionary<string, double>? overrides)
```

## src/linux/Core/TradingTerminal.Core/Backtesting/StrategyParameters.cs
```cs
   13: public sealed class StrategyParameters
   17: public StrategyParameters(IReadOnlyDictionary<string, double>? values = null)
   20: public static StrategyParameters Empty { get; } = new();
   23: public double this[string key] => _values[key];
   25: public bool Contains(string key) => _values.ContainsKey(key);
   27: public double GetOr(string key, double fallback) =>
   30: public int GetInt(string key, int fallback) =>
   33: public bool GetBool(string key, bool fallback) =>
   37: public StrategyParameters With(string key, double value)
   43: public IReadOnlyDictionary<string, double> Values => _values;
```

## src/linux/Core/TradingTerminal.Core/Backtesting/Universe.cs
```cs
   13: public sealed record InstrumentSpec(
   27: public sealed record Universe(IReadOnlyList<InstrumentSpec> Instruments)
   29: public static Universe Single(InstrumentSpec instrument) => new(new[] { instrument });
   31: public static Universe Of(params InstrumentSpec[] instruments) => new(instruments);
   33: public bool IsSingleInstrument => Instruments.Count == 1;
   36: public InstrumentSpec Primary => Instruments[0];
   38: public InstrumentSpec? Find(InstrumentId id) => Instruments.FirstOrDefault(i => i.Id == id);
```

## src/linux/Core/TradingTerminal.Core/Backtesting/VisualTimeline.cs
```cs
    8: public sealed record VisualBar(DateTime TimeUtc, double Open, double High, double Low, double Close);
   12: public sealed record TradeMarker(DateTime TimeUtc, double Price, OrderSide Side, bool IsEntry, InstrumentId Instrument);
   21: public sealed record VisualTimeline(
```
