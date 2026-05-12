using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.App.Research;

/// <summary>
/// Computes a panel of microstructure features from a tick stream, plus the standard
/// quant-research outputs: pairwise Pearson correlation and forward-return decile sort.
/// This is the "feature notebook" loop a real quant researcher runs hundreds of times
/// per week — generate features, check pairwise correlation to deduplicate, run decile
/// sorts to gauge predictive power, then ship the surviving ones to a strategy. Pure
/// math, no I/O, no UI — testable.
/// </summary>
public static class FactorComputation
{
    /// <summary>One aggregated bar over <c>BarTicks</c> consecutive ticks. <c>Returns</c>
    /// is the close-over-close log return; the other fields are averaged within the bar.</summary>
    public sealed record FeatureBar(
        DateTime TimestampUtc,
        double Close,
        double LogReturn,
        double RollingVol,
        double MicropriceDeviation,
        double QueueImbalance,
        double Spread);

    public sealed record CorrelationMatrix(
        IReadOnlyList<string> FeatureNames,
        double[,] Values);

    public sealed record DecileSortResult(
        string FeatureName,
        int ForwardBars,
        IReadOnlyList<DecileRow> Rows);

    public sealed record DecileRow(int Decile, int Count, double LowerEdge, double UpperEdge, double MeanForwardReturn);

    /// <summary>Aggregate raw ticks into N-tick bars and compute features.
    /// <paramref name="volWindow"/> = number of bars used in the rolling-vol estimator.</summary>
    public static IReadOnlyList<FeatureBar> ComputeBars(IReadOnlyList<Tick> ticks, int barTicks = 100, int volWindow = 20)
    {
        var bars = new List<FeatureBar>();
        if (ticks.Count == 0) return bars;

        var returnBuffer = new Queue<double>(volWindow);
        double sumReturn = 0;
        double sumReturnSq = 0;
        double? prevClose = null;

        for (var b = 0; (b + 1) * barTicks <= ticks.Count; b++)
        {
            int start = b * barTicks;
            int end = start + barTicks;

            double microDevSum = 0, qiSum = 0, spreadSum = 0;
            for (var i = start; i < end; i++)
            {
                var t = ticks[i];
                var mid = (t.Bid + t.Ask) * 0.5;
                microDevSum += Microstructure.Microprice(t) - mid;
                qiSum += Microstructure.QueueImbalance(t);
                spreadSum += t.Ask - t.Bid;
            }
            var avgMicroDev = microDevSum / barTicks;
            var avgQi = qiSum / barTicks;
            var avgSpread = spreadSum / barTicks;
            var close = (ticks[end - 1].Bid + ticks[end - 1].Ask) * 0.5;

            double logRet = 0;
            if (prevClose is { } pc && pc > 0)
                logRet = Math.Log(close / pc);
            prevClose = close;

            returnBuffer.Enqueue(logRet);
            sumReturn += logRet;
            sumReturnSq += logRet * logRet;
            while (returnBuffer.Count > volWindow)
            {
                var old = returnBuffer.Dequeue();
                sumReturn -= old;
                sumReturnSq -= old * old;
            }
            double vol = 0;
            if (returnBuffer.Count >= 2)
            {
                var n = returnBuffer.Count;
                var mean = sumReturn / n;
                var var_ = (sumReturnSq - n * mean * mean) / (n - 1);
                vol = var_ > 0 ? Math.Sqrt(var_) : 0;
            }

            bars.Add(new FeatureBar(
                TimestampUtc: ticks[end - 1].TimestampUtc,
                Close: close,
                LogReturn: logRet,
                RollingVol: vol,
                MicropriceDeviation: avgMicroDev,
                QueueImbalance: avgQi,
                Spread: avgSpread));
        }
        return bars;
    }

    /// <summary>Pearson correlation between the standard feature set.</summary>
    public static CorrelationMatrix Correlations(IReadOnlyList<FeatureBar> bars)
    {
        var names = new[] { "LogReturn", "RollingVol", "MicropriceDev", "QueueImbalance", "Spread" };
        Func<FeatureBar, double>[] fs =
        {
            b => b.LogReturn,
            b => b.RollingVol,
            b => b.MicropriceDeviation,
            b => b.QueueImbalance,
            b => b.Spread,
        };
        var n = names.Length;
        var m = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                m[i, j] = i == j ? 1.0 : Pearson(bars, fs[i], fs[j]);
        return new CorrelationMatrix(names, m);
    }

    private static double Pearson(IReadOnlyList<FeatureBar> bars, Func<FeatureBar, double> fa, Func<FeatureBar, double> fb)
    {
        if (bars.Count < 2) return 0;
        double sumA = 0, sumB = 0;
        for (var i = 0; i < bars.Count; i++) { sumA += fa(bars[i]); sumB += fb(bars[i]); }
        var meanA = sumA / bars.Count;
        var meanB = sumB / bars.Count;
        double cov = 0, varA = 0, varB = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            var da = fa(bars[i]) - meanA;
            var db = fb(bars[i]) - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }
        var denom = Math.Sqrt(varA * varB);
        return denom == 0 ? 0 : cov / denom;
    }

    /// <summary>
    /// Sort bars into deciles of <paramref name="featureName"/> and report the mean
    /// forward N-bar return for each. A monotone shape is the textbook signature of a
    /// predictive feature; a flat plot says the feature has no edge at this horizon.
    /// </summary>
    public static DecileSortResult DecileSort(
        IReadOnlyList<FeatureBar> bars,
        string featureName,
        int forwardBars = 5)
    {
        var feature = ResolveFeature(featureName);
        if (bars.Count < 10 || forwardBars <= 0)
            return new DecileSortResult(featureName, forwardBars, Array.Empty<DecileRow>());

        var rows = new List<(double Feat, double FwdRet)>(bars.Count);
        for (var i = 0; i + forwardBars < bars.Count; i++)
        {
            var entry = bars[i].Close;
            var exit = bars[i + forwardBars].Close;
            if (entry <= 0) continue;
            rows.Add((feature(bars[i]), Math.Log(exit / entry)));
        }
        if (rows.Count < 10) return new DecileSortResult(featureName, forwardBars, Array.Empty<DecileRow>());

        var sorted = rows.OrderBy(r => r.Feat).ToList();
        var n = sorted.Count;
        var deciles = new List<DecileRow>();
        for (var d = 0; d < 10; d++)
        {
            int lo = (int)Math.Floor(n * d / 10.0);
            int hi = d == 9 ? n : (int)Math.Floor(n * (d + 1) / 10.0);
            if (hi <= lo) continue;
            double meanFwd = 0;
            for (var i = lo; i < hi; i++) meanFwd += sorted[i].FwdRet;
            meanFwd /= (hi - lo);
            deciles.Add(new DecileRow(d + 1, hi - lo, sorted[lo].Feat, sorted[hi - 1].Feat, meanFwd));
        }
        return new DecileSortResult(featureName, forwardBars, deciles);
    }

    private static Func<FeatureBar, double> ResolveFeature(string name) => name switch
    {
        "LogReturn" => b => b.LogReturn,
        "RollingVol" => b => b.RollingVol,
        "MicropriceDev" => b => b.MicropriceDeviation,
        "QueueImbalance" => b => b.QueueImbalance,
        "Spread" => b => b.Spread,
        _ => throw new ArgumentException($"Unknown feature '{name}'."),
    };
}
