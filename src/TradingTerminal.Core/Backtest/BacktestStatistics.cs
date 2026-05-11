namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Aggregate performance metrics derived from a <see cref="BacktestResult"/>'s trades and
/// equity curve. <see cref="Sharpe"/> and <see cref="Sortino"/> are annualized; the
/// annualization factor is inferred from the average gap between equity samples.
/// <see cref="MaxDrawdown"/> is expressed as a positive fraction of peak equity
/// (e.g. 0.12 = 12% peak-to-trough drop).
/// </summary>
public sealed record BacktestStatistics(
    double TotalReturn,
    double Sharpe,
    double Sortino,
    double MaxDrawdown,
    int TradeCount,
    double WinRate,
    double AvgWin,
    double AvgLoss,
    double ProfitFactor,
    double Expectancy);
