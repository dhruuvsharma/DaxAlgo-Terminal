using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Backtest.Engine.Stats;

/// <summary>
/// Turns a finished run's equity timeline + round-trip ledger into a <see cref="BacktestReport"/>.
/// Risk ratios are computed from the equity curve (mark-to-market, so unrealized risk counts) and
/// annualized from the median sample gap — the same approach as the legacy
/// <c>StatisticsCalculator</c>, but emitted into the open <see cref="MetricSet"/> keyed by
/// <see cref="MetricSet.Keys"/> so new metrics are additive.
/// </summary>
internal static class ReportBuilder
{
    private const double SecondsPerYear = 365.25 * 24 * 60 * 60;

    public static BacktestReport Build(
        RunSummary summary,
        IReadOnlyList<EquitySample> equity,
        IReadOnlyList<RoundTripTrade> trades,
        Universe universe)
    {
        var metrics = new Dictionary<string, double>();

        var (sharpe, sortino, downsideDev) = RiskRatios(equity);
        var (maxDd, ulcer) = DrawdownStats(equity);
        var omega = Omega(equity);
        var cagr = AnnualizeReturn(equity, summary.TotalReturn);

        var wins = trades.Where(t => t.NetPnl > 0).ToList();
        var losses = trades.Where(t => t.NetPnl < 0).ToList();
        var grossWin = wins.Sum(t => t.NetPnl);
        var grossLoss = Math.Abs(losses.Sum(t => t.NetPnl));

        metrics[MetricSet.Keys.Sharpe] = sharpe;
        metrics[MetricSet.Keys.Sortino] = sortino;
        metrics[MetricSet.Keys.DownsideDeviation] = downsideDev;
        metrics[MetricSet.Keys.MaxDrawdown] = maxDd;
        metrics[MetricSet.Keys.UlcerIndex] = ulcer;
        metrics[MetricSet.Keys.Omega] = omega;
        metrics[MetricSet.Keys.Calmar] = maxDd <= 0 ? 0 : cagr / maxDd;
        metrics[MetricSet.Keys.RecoveryFactor] = maxDd <= 0 ? 0 : summary.TotalReturn / maxDd;
        metrics[MetricSet.Keys.WinRate] = trades.Count == 0 ? 0 : (double)wins.Count / trades.Count;
        metrics[MetricSet.Keys.ProfitFactor] = grossLoss == 0 ? (grossWin > 0 ? double.PositiveInfinity : 0) : grossWin / grossLoss;
        metrics[MetricSet.Keys.Expectancy] = trades.Count == 0 ? 0 : trades.Average(t => t.NetPnl);
        metrics[MetricSet.Keys.MaxConsecutiveLosses] = MaxConsecutiveLosses(trades);
        metrics[MetricSet.Keys.AvgHoldingSeconds] = trades.Count == 0 ? 0 : trades.Average(t => t.HoldingTime.TotalSeconds);

        var perInstrument = trades
            .GroupBy(t => t.Instrument)
            .Select(g => new InstrumentReport(
                g.Key,
                g.Sum(t => t.NetPnl),
                g.Count(),
                g.Count(t => t.NetPnl > 0) / (double)g.Count()))
            .ToList();

        return new BacktestReport(summary, new MetricSet(metrics), trades, equity, perInstrument);
    }

    private static (double sharpe, double sortino, double downsideDev) RiskRatios(IReadOnlyList<EquitySample> curve)
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
        var std = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Sum() / returns.Length);
        var downside = returns.Where(r => r < 0).ToArray();
        var downsideStd = downside.Length == 0 ? 0 : Math.Sqrt(downside.Select(r => r * r).Sum() / downside.Length);

        var medianGap = Median(gaps);
        if (medianGap <= 0) return (0, 0, downsideStd);
        var ann = Math.Sqrt(SecondsPerYear / medianGap);

        return (std == 0 ? 0 : mean / std * ann, downsideStd == 0 ? 0 : mean / downsideStd * ann, downsideStd);
    }

    private static (double maxDd, double ulcer) DrawdownStats(IReadOnlyList<EquitySample> curve)
    {
        if (curve.Count == 0) return (0, 0);
        double peak = curve[0].Equity, maxDd = 0, sumSq = 0;
        foreach (var p in curve)
        {
            if (p.Equity > peak) peak = p.Equity;
            if (peak == 0) continue;
            var dd = (peak - p.Equity) / peak;
            if (dd > maxDd) maxDd = dd;
            sumSq += dd * dd;
        }
        return (maxDd, Math.Sqrt(sumSq / curve.Count));
    }

    private static double Omega(IReadOnlyList<EquitySample> curve)
    {
        if (curve.Count < 2) return 0;
        double gain = 0, loss = 0;
        for (var i = 1; i < curve.Count; i++)
        {
            var prev = curve[i - 1].Equity;
            if (prev == 0) continue;
            var r = (curve[i].Equity - prev) / prev;
            if (r > 0) gain += r; else loss -= r;
        }
        return loss == 0 ? (gain > 0 ? double.PositiveInfinity : 0) : gain / loss;
    }

    private static double AnnualizeReturn(IReadOnlyList<EquitySample> curve, double totalReturn)
    {
        if (curve.Count < 2) return 0;
        var seconds = (curve[^1].TimestampUtc - curve[0].TimestampUtc).TotalSeconds;
        if (seconds <= 0) return 0;
        var years = seconds / SecondsPerYear;
        if (years <= 0) return 0;
        var growth = 1.0 + totalReturn;
        return growth <= 0 ? -1.0 : Math.Pow(growth, 1.0 / years) - 1.0;
    }

    private static int MaxConsecutiveLosses(IReadOnlyList<RoundTripTrade> trades)
    {
        int max = 0, run = 0;
        foreach (var t in trades)
        {
            if (t.NetPnl < 0) { run++; if (run > max) max = run; }
            else run = 0;
        }
        return max;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) * 0.5;
    }
}
