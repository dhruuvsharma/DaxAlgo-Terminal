namespace TradingTerminal.Core.Backtest.Fast;

/// <summary>
/// Result emitted by the C++ tick backtester on stdout as JSON. The stats field mirrors
/// <see cref="BacktestStatistics"/> verbatim so the same UI panel renders fast vs managed
/// runs interchangeably. Bulk artefacts (full equity curve, every trade) are returned as
/// paths to temp parquet files the C++ side wrote — the C# side reads them with the same
/// <c>ParquetTickReader</c>-shaped loader. Empty paths mean the C++ side opted not to
/// emit those artefacts (e.g. the strategy fired no trades).
/// </summary>
public sealed record FastBacktestResult(
    BacktestStatistics Stats,
    double EndingCash,
    double TotalFees,
    string? EquityCurveParquetPath,
    string? TradesParquetPath,
    long TicksProcessed,
    double EngineMilliseconds);
