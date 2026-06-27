namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Trade-bootstrap Monte Carlo. Given a sequence of round-trip trade PnLs from a real
/// backtest, resample them N times (with replacement) and report the distribution of
/// final equity / Sharpe / max drawdown. The point: distinguish a strategy with a genuine
/// edge from one that got lucky with a single tape — if 5%-tile Sharpe is still &gt; 0
/// the edge is robust to ordering. Standard pre-deployment sanity check at any quant
/// shop that runs systematic strategies (Bailey, López de Prado, 2014, "The Probability
/// of Backtest Overfitting").
///
/// Note: we resample the TRADE sequence, not the tick sequence. Shuffling ticks breaks
/// time-series structure; shuffling trades keeps each trade's gross PnL intact while
/// asking "does the ORDER of trades matter for the headline stat?" — which is what the
/// drawdown / Sharpe sensitivity is really testing.
/// </summary>
public static class MonteCarlo
{
    public sealed record Result(
        int Simulations,
        double MeanFinalEquity,
        double StdFinalEquity,
        double MeanSharpe,
        double StdSharpe,
        double MeanMaxDrawdown,
        double ProbabilityOfProfit,
        IReadOnlyList<double> FinalEquityPercentiles,   // [P5, P25, P50, P75, P95]
        IReadOnlyList<double> SharpePercentiles,
        IReadOnlyList<double> MaxDrawdownPercentiles);

    /// <summary>
    /// Run <paramref name="simulations"/> resamples. <paramref name="seed"/> = -1 for non-deterministic;
    /// any non-negative value for reproducibility. <paramref name="bootstrap"/> = true resamples
    /// with replacement (standard); false samples without replacement (permutation test,
    /// only useful when trades.Count is large and identical-ish).
    /// </summary>
    public static Result Run(
        IReadOnlyList<double> tradePnls,
        double startingCash,
        int simulations = 10_000,
        int seed = -1,
        bool bootstrap = true)
    {
        if (tradePnls.Count == 0) throw new ArgumentException("Need at least one trade.", nameof(tradePnls));
        if (simulations < 100) throw new ArgumentOutOfRangeException(nameof(simulations), "Use >= 100 simulations for stable percentiles.");

        var rng = seed < 0 ? new Random() : new Random(seed);
        var n = tradePnls.Count;

        var finalEquities = new double[simulations];
        var sharpes = new double[simulations];
        var drawdowns = new double[simulations];
        var profits = 0;

        var resampled = new double[n];
        var equity = new double[n];

        for (var sim = 0; sim < simulations; sim++)
        {
            if (bootstrap)
            {
                for (var i = 0; i < n; i++) resampled[i] = tradePnls[rng.Next(n)];
            }
            else
            {
                for (var i = 0; i < n; i++) resampled[i] = tradePnls[i];
                Shuffle(resampled, rng);
            }

            double eq = startingCash;
            double peak = startingCash;
            double maxDd = 0;
            for (var i = 0; i < n; i++)
            {
                eq += resampled[i];
                equity[i] = eq;
                if (eq > peak) peak = eq;
                if (peak > 0)
                {
                    var dd = (peak - eq) / peak;
                    if (dd > maxDd) maxDd = dd;
                }
            }

            finalEquities[sim] = eq;
            drawdowns[sim] = maxDd;

            // Per-trade Sharpe (no annualisation — trade-count-based). User can scale later.
            double mean = 0, var = 0;
            for (var i = 0; i < n; i++) mean += resampled[i];
            mean /= n;
            for (var i = 0; i < n; i++) { var d = resampled[i] - mean; var += d * d; }
            var sd = Math.Sqrt(var / Math.Max(1, n - 1));
            sharpes[sim] = sd == 0 ? 0 : mean / sd * Math.Sqrt(n);

            if (eq > startingCash) profits++;
        }

        return new Result(
            Simulations: simulations,
            MeanFinalEquity: Mean(finalEquities),
            StdFinalEquity: Std(finalEquities),
            MeanSharpe: Mean(sharpes),
            StdSharpe: Std(sharpes),
            MeanMaxDrawdown: Mean(drawdowns),
            ProbabilityOfProfit: (double)profits / simulations,
            FinalEquityPercentiles: Percentiles(finalEquities),
            SharpePercentiles: Percentiles(sharpes),
            MaxDrawdownPercentiles: Percentiles(drawdowns));
    }

    private static void Shuffle(double[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    private static double Mean(double[] a)
    {
        double s = 0; foreach (var v in a) s += v;
        return a.Length == 0 ? 0 : s / a.Length;
    }

    private static double Std(double[] a)
    {
        if (a.Length < 2) return 0;
        var m = Mean(a);
        double v = 0; foreach (var x in a) { var d = x - m; v += d * d; }
        return Math.Sqrt(v / (a.Length - 1));
    }

    private static IReadOnlyList<double> Percentiles(double[] a)
    {
        var sorted = a.OrderBy(x => x).ToArray();
        return new[]
        {
            sorted[(int)(sorted.Length * 0.05)],
            sorted[(int)(sorted.Length * 0.25)],
            sorted[(int)(sorted.Length * 0.50)],
            sorted[(int)(sorted.Length * 0.75)],
            sorted[(int)(sorted.Length * 0.95)],
        };
    }
}
