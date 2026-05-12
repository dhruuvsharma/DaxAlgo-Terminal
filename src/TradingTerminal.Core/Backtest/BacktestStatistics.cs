namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Aggregate performance metrics derived from a <see cref="BacktestResult"/>'s trades and
/// equity curve. <see cref="Sharpe"/> and <see cref="Sortino"/> are annualized; the
/// annualization factor is inferred from the average gap between equity samples.
/// <see cref="MaxDrawdown"/> is expressed as a positive fraction of peak equity
/// (e.g. 0.12 = 12% peak-to-trough drop).
/// </summary>
/// <remarks>
/// Field semantics for the optional metrics:
///   Calmar               — annualised CAGR / max-drawdown; higher is better, undefined when MDD = 0.
///   Omega                — Σ gains / Σ losses on the return distribution (threshold = 0).
///   DownsideDeviation    — sqrt-mean of squared negative returns; used inside Sortino, exposed here for inspection.
///   RecoveryFactor       — total return / max drawdown; sanity check beat-by-luck systems fail.
///   MaxConsecutiveLosses — longest streak of consecutive losing trades.
///   UlcerIndex           — RMS of percentage drawdowns over the equity curve (Martin's tail-aware analogue of stddev).
/// </remarks>
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
    double Expectancy,
    double Calmar = 0,
    double Omega = 0,
    double DownsideDeviation = 0,
    double RecoveryFactor = 0,
    int MaxConsecutiveLosses = 0,
    double UlcerIndex = 0);
