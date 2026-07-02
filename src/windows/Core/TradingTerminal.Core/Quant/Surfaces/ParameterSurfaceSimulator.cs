using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>
/// The Mode-A cell engine: a deliberately small, deterministic long/flat strategy kernel that
/// every sweepable parameter in <see cref="SurfaceAxisCatalog.Parameters"/> plugs into. Baseline
/// signal is an MA cross (fast &gt; slow ⇒ long, else flat); optional entry filters (RSI &gt; 50,
/// ROC &gt; 0) and optional exits (fixed stop-loss %, take-profit %, ATR trailing stop) switch on
/// when their parameter is &gt; 0. Signals are computed on bar close and applied to the NEXT bar's
/// return — no look-ahead. One call = one grid cell, so it must stay allocation-light and
/// thread-safe (it is: pure function of its inputs).
/// </summary>
public static class ParameterSurfaceSimulator
{
    /// <summary>Runs the kernel over <paramref name="bars"/> with per-cell parameter overrides
    /// (ids from <see cref="SurfaceAxisCatalog.Parameters"/>); anything absent uses its default.
    /// Returns the per-bar strategy returns aligned with the underlying's returns (benchmark),
    /// per-bar dollar volumes, and the closed-trade return list.</summary>
    public static SurfaceCellSample Run(
        IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<string, double> overrides,
        double periodsPerYear)
    {
        if (bars.Count < 3) return SurfaceCellSample.Empty with { PeriodsPerYear = periodsPerYear };

        double P(string id)
        {
            if (overrides.TryGetValue(id, out var v)) return v;
            return SurfaceAxisCatalog.ResolveParameter(id)!.DefaultValue;
        }

        var fast = Math.Max(1, (int)Math.Round(P("fastma")));
        var slow = Math.Max(2, (int)Math.Round(P("slowma")));
        var rsiLen = (int)Math.Round(P("rsilen"));
        var rocLen = (int)Math.Round(P("roclen"));
        var stopLoss = P("stoploss");
        var takeProfit = P("takeprofit");
        var atrMult = P("atrmult");
        const int atrLen = 14;

        // Degenerate sweeps (fast ≥ slow has no cross semantics) return an empty sample so the
        // metric shows NaN — an honest gap instead of a fake flat spot.
        if (fast >= slow) return SurfaceCellSample.Empty with { PeriodsPerYear = periodsPerYear };

        var n = bars.Count;
        var closes = new double[n];
        for (var i = 0; i < n; i++) closes[i] = bars[i].Close;

        var fastMa = Sma(closes, fast);
        var slowMa = Sma(closes, slow);
        var rsi = rsiLen > 0 ? Rsi(closes, rsiLen) : null;
        var atr = atrMult > 0 ? Atr(bars, atrLen) : null;

        var stratReturns = new List<double>(n);
        var benchReturns = new List<double>(n);
        var dollarVolumes = new List<double>(n);
        var tradeReturns = new List<double>();

        var inPosition = false;
        double entryPrice = 0, trailStop = 0;

        for (var i = 1; i < n; i++)
        {
            var barReturn = closes[i - 1] > 0 ? closes[i] / closes[i - 1] - 1 : 0;
            stratReturns.Add(inPosition ? barReturn : 0);
            benchReturns.Add(barReturn);
            dollarVolumes.Add(closes[i] * Math.Max(bars[i].Volume, 0));

            // ── Exits (evaluated on the close of bar i, position drops from bar i+1) ─────────
            if (inPosition)
            {
                var gain = closes[i] / entryPrice - 1;
                var exit = false;

                if (stopLoss > 0 && gain <= -stopLoss) exit = true;
                if (takeProfit > 0 && gain >= takeProfit) exit = true;
                if (atr is not null && !double.IsNaN(atr[i]))
                {
                    trailStop = Math.Max(trailStop, closes[i] - atrMult * atr[i]);
                    if (closes[i] <= trailStop) exit = true;
                }
                if (!double.IsNaN(fastMa[i]) && !double.IsNaN(slowMa[i]) && fastMa[i] < slowMa[i]) exit = true;

                if (exit)
                {
                    tradeReturns.Add(gain);
                    inPosition = false;
                    continue;
                }
            }

            // ── Entries ──────────────────────────────────────────────────────────────────────
            if (!inPosition && !double.IsNaN(fastMa[i]) && !double.IsNaN(slowMa[i]) && fastMa[i] > slowMa[i])
            {
                if (rsi is not null && (double.IsNaN(rsi[i]) || rsi[i] <= 50)) continue;
                if (rocLen > 0 && (i < rocLen || closes[i] <= closes[i - rocLen])) continue;

                inPosition = true;
                entryPrice = closes[i];
                trailStop = atr is not null && !double.IsNaN(atr[i]) ? closes[i] - atrMult * atr[i] : 0;
            }
        }

        if (inPosition && entryPrice > 0)
            tradeReturns.Add(closes[n - 1] / entryPrice - 1);

        return new SurfaceCellSample(
            stratReturns.ToArray(),
            benchReturns.ToArray(),
            dollarVolumes.ToArray(),
            tradeReturns.ToArray(),
            periodsPerYear);
    }

    // ── Vectorized indicator helpers (NaN until warm) ─────────────────────────────────────────

    internal static double[] Sma(double[] values, int period)
    {
        var result = new double[values.Length];
        Array.Fill(result, double.NaN);
        var sum = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }

    internal static double[] Rsi(double[] closes, int period)
    {
        var result = new double[closes.Length];
        Array.Fill(result, double.NaN);
        double avgGain = 0, avgLoss = 0;
        for (var i = 1; i < closes.Length; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var gain = delta > 0 ? delta : 0;
            var loss = delta < 0 ? -delta : 0;
            if (i <= period)
            {
                avgGain += gain / period;
                avgLoss += loss / period;
                if (i < period) continue;
            }
            else
            {
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }
            result[i] = avgLoss <= 0 ? (avgGain > 0 ? 100 : 50) : 100 - 100 / (1 + avgGain / avgLoss);
        }
        return result;
    }

    internal static double[] Atr(IReadOnlyList<Bar> bars, int period)
    {
        var result = new double[bars.Count];
        Array.Fill(result, double.NaN);
        double atr = 0;
        for (var i = 1; i < bars.Count; i++)
        {
            var tr = Math.Max(bars[i].High - bars[i].Low,
                     Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close),
                              Math.Abs(bars[i].Low - bars[i - 1].Close)));
            if (i <= period)
            {
                atr += tr / period;
                if (i < period) continue;
            }
            else
            {
                atr = (atr * (period - 1) + tr) / period;
            }
            result[i] = atr;
        }
        return result;
    }
}
