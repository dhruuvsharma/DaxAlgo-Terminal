namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Output of a single backtest run. <see cref="Stats"/> is null until Phase 4 wires the
/// statistics pass; the raw <see cref="Trades"/> and <see cref="EquityCurve"/> are always
/// present and sufficient to compute any stat downstream.
/// </summary>
public sealed record BacktestResult(
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<EquityPoint> EquityCurve,
    double StartingCash,
    double EndingCash,
    BacktestStatistics? Stats = null);
