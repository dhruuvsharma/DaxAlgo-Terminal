using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Computes <see cref="BacktestStatistics"/> from a finished run. All ratios are computed
/// from the equity curve (mark-to-market) rather than per-trade PnL — this captures unrealized
/// risk while a position is open and matches how live performance is usually reported.
///
/// The annualization factor for Sharpe/Sortino is inferred from the median gap between
/// equity samples: sqrt(secondsPerYear / sampleSeconds). One-minute samples on a 24/5 future
/// market come out to ~sqrt(525,600) ≈ 725; on stock-session-only data, the math still works
/// because the sample gaps reflect the actual sampling cadence.
/// </summary>
public static class StatisticsCalculator
{
    private const double SecondsPerYear = 365.25 * 24 * 60 * 60;

    public static BacktestStatistics Calculate(BacktestResult result)
    {
        var trades = result.Trades;
        var curve = result.EquityCurve;

        var totalReturn = result.StartingCash == 0
            ? 0
            : (result.EndingCash - result.StartingCash) / result.StartingCash;

        var (sharpe, sortino, downsideDev) = ComputeRiskRatios(curve);
        var (maxDrawdown, ulcer) = ComputeDrawdownStats(curve);

        var wins = trades.Where(t => t.GrossPnl > 0).ToList();
        var losses = trades.Where(t => t.GrossPnl < 0).ToList();

        var winRate = trades.Count == 0 ? 0 : (double)wins.Count / trades.Count;
        var avgWin = wins.Count == 0 ? 0 : wins.Average(t => t.GrossPnl);
        var avgLoss = losses.Count == 0 ? 0 : losses.Average(t => t.GrossPnl);

        var totalWins = wins.Sum(t => t.GrossPnl);
        var totalLosses = Math.Abs(losses.Sum(t => t.GrossPnl));
        var profitFactor = totalLosses == 0
            ? (totalWins > 0 ? double.PositiveInfinity : 0)
            : totalWins / totalLosses;

        var expectancy = trades.Count == 0 ? 0 : trades.Average(t => t.GrossPnl);

        var cagr = AnnualizeReturn(curve, totalReturn);
        var calmar = maxDrawdown <= 0 ? 0 : cagr / maxDrawdown;
        var omega = ComputeOmega(curve);
        var recoveryFactor = maxDrawdown <= 0 ? 0 : totalReturn / maxDrawdown;
        var maxConsecutiveLosses = ComputeMaxConsecutiveLosses(trades);

        return new BacktestStatistics(
            TotalReturn: totalReturn,
            Sharpe: sharpe,
            Sortino: sortino,
            MaxDrawdown: maxDrawdown,
            TradeCount: trades.Count,
            WinRate: winRate,
            AvgWin: avgWin,
            AvgLoss: avgLoss,
            ProfitFactor: profitFactor,
            Expectancy: expectancy,
            Calmar: calmar,
            Omega: omega,
            DownsideDeviation: downsideDev,
            RecoveryFactor: recoveryFactor,
            MaxConsecutiveLosses: maxConsecutiveLosses,
            UlcerIndex: ulcer);
    }

    private static (double sharpe, double sortino, double downsideDev) ComputeRiskRatios(IReadOnlyList<EquityPoint> curve)
    {
        if (curve.Count < 2) return (0, 0, 0);

        var returns = new double[curve.Count - 1];
        var gaps = new double[curve.Count - 1];
        for (var i = 1; i < curve.Count; i++)
        {
            var prev = curve[i - 1].Equity;
            returns[i - 1] = prev == 0 ? 0 : (curve[i].Equity - prev) / prev;
            gaps[i - 1] = (curve[i].TimestampUtc - curve[i - 1].TimestampUtc).TotalSeconds;
        }

        var mean = returns.Average();
        var variance = returns.Select(r => (r - mean) * (r - mean)).Sum() / returns.Length;
        var std = Math.Sqrt(variance);

        var downside = returns.Where(r => r < 0).ToArray();
        var downsideStd = downside.Length == 0
            ? 0
            : Math.Sqrt(downside.Select(r => r * r).Sum() / downside.Length);

        var medianGap = Median(gaps);
        if (medianGap <= 0) return (0, 0, downsideStd);
        var annualization = Math.Sqrt(SecondsPerYear / medianGap);

        var sharpe = std == 0 ? 0 : mean / std * annualization;
        var sortino = downsideStd == 0 ? 0 : mean / downsideStd * annualization;
        return (sharpe, sortino, downsideStd);
    }

    private static (double maxDd, double ulcer) ComputeDrawdownStats(IReadOnlyList<EquityPoint> curve)
    {
        if (curve.Count == 0) return (0, 0);
        var peak = curve[0].Equity;
        var maxDd = 0d;
        var sumSqDd = 0d;
        foreach (var p in curve)
        {
            if (p.Equity > peak) peak = p.Equity;
            if (peak == 0) continue;
            var dd = (peak - p.Equity) / peak;
            if (dd > maxDd) maxDd = dd;
            sumSqDd += dd * dd;
        }
        var ulcer = Math.Sqrt(sumSqDd / curve.Count);
        return (maxDd, ulcer);
    }

    private static double AnnualizeReturn(IReadOnlyList<EquityPoint> curve, double totalReturn)
    {
        if (curve.Count < 2) return 0;
        var seconds = (curve[^1].TimestampUtc - curve[0].TimestampUtc).TotalSeconds;
        if (seconds <= 0) return 0;
        var years = seconds / SecondsPerYear;
        if (years <= 0) return 0;
        // Use log-return → exp form to keep stability for tiny intervals.
        var growth = 1.0 + totalReturn;
        if (growth <= 0) return -1.0;
        return Math.Pow(growth, 1.0 / years) - 1.0;
    }

    private static double ComputeOmega(IReadOnlyList<EquityPoint> curve)
    {
        if (curve.Count < 2) return 0;
        double gain = 0, loss = 0;
        for (var i = 1; i < curve.Count; i++)
        {
            var prev = curve[i - 1].Equity;
            if (prev == 0) continue;
            var r = (curve[i].Equity - prev) / prev;
            if (r > 0) gain += r;
            else loss -= r;
        }
        return loss == 0 ? (gain > 0 ? double.PositiveInfinity : 0) : gain / loss;
    }

    private static int ComputeMaxConsecutiveLosses(IReadOnlyList<TradingTerminal.Core.Backtest.Trade> trades)
    {
        var maxRun = 0;
        var run = 0;
        foreach (var t in trades)
        {
            if (t.GrossPnl < 0)
            {
                run++;
                if (run > maxRun) maxRun = run;
            }
            else
            {
                run = 0;
            }
        }
        return maxRun;
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var n = sorted.Length;
        if (n == 0) return 0;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) * 0.5;
    }
}
