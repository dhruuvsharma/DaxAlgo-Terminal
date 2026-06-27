using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization;

/// <summary>Scores a finished run by an <see cref="OptimizationCriterion"/>, normalized so higher is
/// always better (drawdown is negated). NaN sinks to the bottom (degenerate run); a legitimate +∞
/// (e.g. profit factor with no losses) maps to a finite max so it ranks top but stays sortable.</summary>
internal static class Criteria
{
    public static double Score(OptimizationCriterion criterion, BacktestReport report)
    {
        var raw = criterion switch
        {
            OptimizationCriterion.NetProfit => report.Summary.NetProfit,
            OptimizationCriterion.Sharpe => report.Metrics.Sharpe,
            OptimizationCriterion.Sortino => report.Metrics.Sortino,
            OptimizationCriterion.ProfitFactor => report.Metrics.ProfitFactor,
            OptimizationCriterion.Calmar => report.Metrics.Calmar,
            OptimizationCriterion.Expectancy => report.Metrics.Expectancy,
            OptimizationCriterion.WinRate => report.Metrics.WinRate,
            OptimizationCriterion.MinDrawdown => -report.Metrics.MaxDrawdown,
            _ => double.NegativeInfinity,
        };
        if (double.IsNaN(raw)) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(raw)) return double.MaxValue;
        return raw;
    }
}
