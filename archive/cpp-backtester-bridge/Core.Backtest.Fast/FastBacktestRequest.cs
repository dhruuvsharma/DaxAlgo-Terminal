namespace TradingTerminal.Core.Backtest.Fast;

/// <summary>
/// Input to the out-of-process C++ tick backtester. Serialised to JSON, written to the
/// child process's stdin. The shape is the polyglot.md contract — both sides widen
/// together.
///
/// Bulk data (the tick tape) crosses as a file path, not embedded JSON: parquet is the
/// agreed columnar format on both sides, and a 10M-tick payload would be insane to
/// stringify. Per-strategy parameters are a free-form dictionary because the C++ engine
/// owns the parameter schemas; the C# side does no validation beyond well-formed JSON.
/// </summary>
public sealed record FastBacktestRequest(
    string StrategyId,
    string Symbol,
    string TickDataParquetPath,
    double TickSize,
    double ContractMultiplier,
    double StartingCash,
    int SlippageTicks = 0,
    double TakerFeePerUnit = 0,
    double MakerRebatePerUnit = 0,
    double FeeBps = 0,
    long? MaxAbsolutePositionPerSymbol = null,
    double? DailyLossCap = null,
    IReadOnlyDictionary<string, double>? Params = null);
