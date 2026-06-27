using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData.AdvancedRegime;

/// <summary>
/// Pure, allocation-light OHLCV bar-series indicators backing the Advanced Live Market Regime
/// dashboard. Everything here is a static function over an <see cref="IReadOnlyList{Bar}"/>; there
/// is no streaming state. Returns <see cref="double.NaN"/> (or null) when a window has too few bars.
///
/// Reuses the streaming primitives in <see cref="Indicators"/> (SMA / stdev / RSI) for the price
/// series that map cleanly onto them; the OHLC-specific maths (Wilder ATR, CCI, SuperTrend, VWAP)
/// live here because <see cref="Indicators"/> is single-price / tick-oriented.
/// </summary>
public static class AdvancedRegimeBarIndicators
{
    /// <summary>SMA over the final <paramref name="length"/> elements of <paramref name="values"/>;
    /// NaN if fewer than <paramref name="length"/> finite values are available.</summary>
    public static double SmaTail(IReadOnlyList<double> values, int length)
    {
        if (values is null || length <= 0 || values.Count < length)
            return double.NaN;
        double sum = 0;
        for (int i = values.Count - length; i < values.Count; i++)
        {
            var v = values[i];
            if (double.IsNaN(v)) return double.NaN;
            sum += v;
        }
        return sum / length;
    }

    /// <summary>Closing-price series for convenience.</summary>
    public static double[] Closes(IReadOnlyList<Bar> bars)
    {
        var arr = new double[bars.Count];
        for (int i = 0; i < bars.Count; i++) arr[i] = bars[i].Close;
        return arr;
    }

    /// <summary>
    /// Wilder ATR over the bar series. TR_t = max(H-L, |H-prevClose|, |L-prevClose|); ATR is seeded
    /// with the SMA of the first <paramref name="length"/> TRs, then Wilder-smoothed:
    /// ATR_t = (ATR_{t-1}·(n-1) + TR_t) / n. Returns the full series aligned to <paramref name="bars"/>,
    /// NaN until the seed bar.
    /// </summary>
    public static double[] TrueRangeAtr(IReadOnlyList<Bar> bars, int length)
    {
        var n = bars.Count;
        var atr = new double[n];
        for (int i = 0; i < n; i++) atr[i] = double.NaN;
        if (n == 0 || length <= 0) return atr;

        var tr = new double[n];
        tr[0] = bars[0].High - bars[0].Low;
        for (int i = 1; i < n; i++)
        {
            var prevClose = bars[i - 1].Close;
            var hl = bars[i].High - bars[i].Low;
            var hc = Math.Abs(bars[i].High - prevClose);
            var lc = Math.Abs(bars[i].Low - prevClose);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        if (n < length) return atr;

        // Seed at index (length-1) with SMA of the first `length` TRs.
        double seed = 0;
        for (int i = 0; i < length; i++) seed += tr[i];
        seed /= length;
        atr[length - 1] = seed;

        for (int i = length; i < n; i++)
            atr[i] = (atr[i - 1] * (length - 1) + tr[i]) / length;

        return atr;
    }

    /// <summary>
    /// EMA-based MACD over a price series. EMAs are seeded with the SMA of their first window. The
    /// signal line is an EMA of the MACD line (seeded once the MACD line has <paramref name="signal"/>
    /// points). Returns the last-bar (macdLine, signalLine, histogram); any component is NaN when
    /// insufficient data.
    /// </summary>
    public static (double MacdLine, double SignalLine, double Histogram) Macd(
        IReadOnlyList<double> closes, int fast, int slow, int signal)
    {
        if (closes is null || fast <= 0 || slow <= 0 || signal <= 0 || closes.Count < slow)
            return (double.NaN, double.NaN, double.NaN);

        var fastEma = Ema(closes, fast);
        var slowEma = Ema(closes, slow);

        var n = closes.Count;
        var macd = new double[n];
        for (int i = 0; i < n; i++)
        {
            macd[i] = (double.IsNaN(fastEma[i]) || double.IsNaN(slowEma[i]))
                ? double.NaN
                : fastEma[i] - slowEma[i];
        }

        // Signal EMA over the finite portion of the MACD line.
        int firstFinite = -1;
        for (int i = 0; i < n; i++) { if (!double.IsNaN(macd[i])) { firstFinite = i; break; } }
        if (firstFinite < 0) return (double.NaN, double.NaN, double.NaN);

        var macdTail = new double[n - firstFinite];
        for (int i = firstFinite; i < n; i++) macdTail[i - firstFinite] = macd[i];

        var sig = Ema(macdTail, signal);
        var macdLine = macd[n - 1];
        var signalLine = sig[sig.Length - 1];
        var hist = (double.IsNaN(macdLine) || double.IsNaN(signalLine)) ? double.NaN : macdLine - signalLine;
        return (macdLine, signalLine, hist);
    }

    /// <summary>EMA series seeded with the SMA of the first <paramref name="period"/> values, NaN
    /// before the seed.</summary>
    public static double[] Ema(IReadOnlyList<double> values, int period)
    {
        var n = values.Count;
        var ema = new double[n];
        for (int i = 0; i < n; i++) ema[i] = double.NaN;
        if (n < period || period <= 0) return ema;

        double seed = 0;
        for (int i = 0; i < period; i++) seed += values[i];
        seed /= period;
        ema[period - 1] = seed;

        var alpha = 2.0 / (period + 1);
        for (int i = period; i < n; i++)
            ema[i] = alpha * values[i] + (1 - alpha) * ema[i - 1];

        return ema;
    }

    /// <summary>
    /// CCI over the last bar. TP = (H+L+C)/3; CCI = (TP - SMA(TP,len)) / (0.015 * mean-abs-deviation
    /// of TP over len). NaN when fewer than <paramref name="length"/> bars or the deviation is zero.
    /// </summary>
    public static double Cci(IReadOnlyList<Bar> bars, int length)
    {
        if (bars is null || length <= 0 || bars.Count < length)
            return double.NaN;

        var start = bars.Count - length;
        double smaTp = 0;
        var tp = new double[length];
        for (int i = 0; i < length; i++)
        {
            var b = bars[start + i];
            tp[i] = (b.High + b.Low + b.Close) / 3.0;
            smaTp += tp[i];
        }
        smaTp /= length;

        double mad = 0;
        for (int i = 0; i < length; i++) mad += Math.Abs(tp[i] - smaTp);
        mad /= length;
        if (mad == 0) return double.NaN;

        var currentTp = tp[length - 1];
        return (currentTp - smaTp) / (0.015 * mad);
    }

    /// <summary>
    /// SuperTrend for the last bar. Standard band-ratcheting algorithm: basic upper/lower bands from
    /// (H+L)/2 ± factor·ATR, ratcheted so they only tighten until price closes through them, then the
    /// direction flips. Returns the SuperTrend line value and whether the trend is bullish
    /// (Pine's <c>dir == -1</c> ⇒ uptrend). NaN line / false when insufficient data.
    /// </summary>
    public static (double Line, bool IsBullish) SuperTrend(IReadOnlyList<Bar> bars, double factor, int atrLength)
    {
        var n = bars.Count;
        if (n == 0 || atrLength <= 0 || n < atrLength)
            return (double.NaN, false);

        var atr = TrueRangeAtr(bars, atrLength);

        // Find first index with finite ATR.
        int start = -1;
        for (int i = 0; i < n; i++) { if (!double.IsNaN(atr[i])) { start = i; break; } }
        if (start < 0) return (double.NaN, false);

        double finalUpper = 0, finalLower = 0;
        double superTrend = 0;
        bool isBullish = true;
        bool initialised = false;

        for (int i = start; i < n; i++)
        {
            var mid = (bars[i].High + bars[i].Low) / 2.0;
            var basicUpper = mid + factor * atr[i];
            var basicLower = mid - factor * atr[i];

            if (!initialised)
            {
                finalUpper = basicUpper;
                finalLower = basicLower;
                isBullish = bars[i].Close >= basicLower;
                superTrend = isBullish ? finalLower : finalUpper;
                initialised = true;
                continue;
            }

            var prevClose = bars[i - 1].Close;

            // Ratchet the bands: each only tightens until the prior close breaks through it.
            finalUpper = (basicUpper < finalUpper || prevClose > finalUpper) ? basicUpper : finalUpper;
            finalLower = (basicLower > finalLower || prevClose < finalLower) ? basicLower : finalLower;

            var close = bars[i].Close;
            if (isBullish)
            {
                // Uptrend tracks the lower band; flip down only if the close breaks below it.
                if (close < finalLower)
                {
                    isBullish = false;
                    superTrend = finalUpper;
                }
                else
                {
                    superTrend = finalLower;
                }
            }
            else
            {
                // Downtrend tracks the upper band; flip up only if the close breaks above it.
                if (close > finalUpper)
                {
                    isBullish = true;
                    superTrend = finalLower;
                }
                else
                {
                    superTrend = finalUpper;
                }
            }
        }

        return (superTrend, isBullish);
    }

    /// <summary>
    /// Session VWAP: cumulative Σ(HLC3·V)/ΣV over the bars sharing the last bar's UTC calendar day,
    /// starting at that day's first bar. NaN when volume in the session is zero or no bars.
    /// </summary>
    public static double SessionVwap(IReadOnlyList<Bar> bars)
    {
        if (bars is null || bars.Count == 0) return double.NaN;
        var sessionDay = bars[^1].TimestampUtc.Date;

        double pv = 0;
        double vol = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            if (b.TimestampUtc.Date != sessionDay) continue;
            var hlc3 = (b.High + b.Low + b.Close) / 3.0;
            pv += hlc3 * b.Volume;
            vol += b.Volume;
        }
        return vol > 0 ? pv / vol : double.NaN;
    }

    /// <summary>
    /// Point-of-control approximation: HLC3 of the highest-volume bar among the last
    /// <paramref name="lookback"/> bars. NaN if no bars.
    /// </summary>
    public static double PocApprox(IReadOnlyList<Bar> bars, int lookback)
    {
        if (bars is null || bars.Count == 0 || lookback <= 0) return double.NaN;
        var start = Math.Max(0, bars.Count - lookback);
        long bestVol = long.MinValue;
        double poc = double.NaN;
        for (int i = start; i < bars.Count; i++)
        {
            if (bars[i].Volume > bestVol)
            {
                bestVol = bars[i].Volume;
                poc = (bars[i].High + bars[i].Low + bars[i].Close) / 3.0;
            }
        }
        return poc;
    }

    /// <summary>
    /// Range position: (close - lowest low) / (highest high - lowest low) × 100 over the last
    /// <paramref name="length"/> bars. Returns 50 when the range is flat; NaN when too few bars.
    /// </summary>
    public static double RangePosition(IReadOnlyList<Bar> bars, int length)
    {
        if (bars is null || length <= 0 || bars.Count < length) return double.NaN;
        var start = bars.Count - length;
        double hh = double.MinValue, ll = double.MaxValue;
        for (int i = start; i < bars.Count; i++)
        {
            if (bars[i].High > hh) hh = bars[i].High;
            if (bars[i].Low < ll) ll = bars[i].Low;
        }
        var range = hh - ll;
        if (range <= 0) return 50;
        return (bars[^1].Close - ll) / range * 100.0;
    }

    /// <summary>Signed per-bar delta: +volume on an up bar, -volume on a down bar, 0 on a doji.</summary>
    public static double BarDelta(Bar bar)
    {
        if (bar.Close > bar.Open) return bar.Volume;
        if (bar.Close < bar.Open) return -(double)bar.Volume;
        return 0;
    }
}
