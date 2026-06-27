using System.Collections.ObjectModel;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.UI;

/// <summary>
/// Bar-level indicator computation used by strategy VMs for chart visualisation.
/// These are *not* the tick-level indicators the engine strategies run on — they're
/// a visual approximation computed on the 15s aggregated bars. The actual trading
/// signal runs inside the wrapped <see cref="Core.Backtest.IBacktestStrategy"/> at
/// tick granularity, just like in backtest.
/// </summary>
public static class BarIndicators
{
    public static double[] Sma(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count == 0) return Array.Empty<double>();
        var result = new double[bars.Count];
        var ind = new Indicators.SimpleMovingAverage(period);
        for (var i = 0; i < bars.Count; i++)
        {
            ind.Push(bars[i].Close);
            result[i] = ind.IsReady ? ind.Value : double.NaN;
        }
        return result;
    }

    public static double[] Ema(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count == 0) return Array.Empty<double>();
        var result = new double[bars.Count];
        var ind = new Indicators.ExponentialMovingAverage(period);
        for (var i = 0; i < bars.Count; i++)
        {
            ind.Push(bars[i].Close);
            result[i] = i >= period - 1 ? ind.Value : double.NaN;
        }
        return result;
    }

    /// <summary>Returns (mean, stdev, upper, lower) arrays aligned with bars.</summary>
    public static (double[] mean, double[] sd, double[] upper, double[] lower) Bollinger(
        IReadOnlyList<Bar> bars, int period, double stdMult)
    {
        var mean = new double[bars.Count];
        var sd = new double[bars.Count];
        var upper = new double[bars.Count];
        var lower = new double[bars.Count];
        var stat = new Indicators.RollingStdev(period);
        for (var i = 0; i < bars.Count; i++)
        {
            stat.Push(bars[i].Close);
            if (stat.IsReady)
            {
                mean[i] = stat.Mean;
                sd[i] = stat.Value;
                upper[i] = stat.Mean + stdMult * stat.Value;
                lower[i] = stat.Mean - stdMult * stat.Value;
            }
            else { mean[i] = sd[i] = upper[i] = lower[i] = double.NaN; }
        }
        return (mean, sd, upper, lower);
    }

    public static double[] Rsi(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count == 0) return Array.Empty<double>();
        var result = new double[bars.Count];
        var ind = new Indicators.RelativeStrengthIndex(period);
        for (var i = 0; i < bars.Count; i++)
        {
            ind.Push(bars[i].Close);
            result[i] = ind.IsReady ? ind.Value : double.NaN;
        }
        return result;
    }

    public static (double[] macd, double[] signal) Macd(
        IReadOnlyList<Bar> bars, int fastPeriod, int slowPeriod, int signalPeriod)
    {
        var macd = new double[bars.Count];
        var signal = new double[bars.Count];
        var fast = new Indicators.ExponentialMovingAverage(fastPeriod);
        var slow = new Indicators.ExponentialMovingAverage(slowPeriod);
        var sig = new Indicators.ExponentialMovingAverage(signalPeriod);
        for (var i = 0; i < bars.Count; i++)
        {
            var c = bars[i].Close;
            fast.Push(c);
            slow.Push(c);
            if (!slow.IsReady) { macd[i] = signal[i] = double.NaN; continue; }
            var m = fast.Value - slow.Value;
            macd[i] = m;
            sig.Push(m);
            signal[i] = sig.IsReady ? sig.Value : double.NaN;
        }
        return (macd, signal);
    }

    /// <summary>Rolling z-score of close vs window mean/stdev.</summary>
    public static double[] ZScore(IReadOnlyList<Bar> bars, int window)
    {
        var result = new double[bars.Count];
        var stat = new Indicators.RollingStdev(window);
        for (var i = 0; i < bars.Count; i++)
        {
            stat.Push(bars[i].Close);
            if (stat.IsReady && stat.Value > 0)
                result[i] = (bars[i].Close - stat.Mean) / stat.Value;
            else
                result[i] = double.NaN;
        }
        return result;
    }

    /// <summary>Rolling realised vol (stdev of log returns) over a window.</summary>
    public static double[] RealisedVol(IReadOnlyList<Bar> bars, int window)
    {
        var result = new double[bars.Count];
        var stat = new Indicators.RollingStdev(window);
        for (var i = 0; i < bars.Count; i++)
        {
            if (i == 0) { result[i] = double.NaN; continue; }
            var r = Math.Log(bars[i].Close / bars[i - 1].Close);
            stat.Push(r);
            result[i] = stat.IsReady ? stat.Value : double.NaN;
        }
        return result;
    }

    public static double[] Atr(IReadOnlyList<Bar> bars, int period)
    {
        var result = new double[bars.Count];
        var ind = new Indicators.AverageTrueRange(period);
        for (var i = 0; i < bars.Count; i++)
        {
            ind.Push(bars[i].Close);
            result[i] = ind.IsReady ? ind.Value : double.NaN;
        }
        return result;
    }

    public static double[] BarTimestamps(IReadOnlyList<Bar> bars)
    {
        var xs = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++) xs[i] = bars[i].TimestampUtc.ToOADate();
        return xs;
    }

    public static double[] BarCloses(IReadOnlyList<Bar> bars)
    {
        var ys = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++) ys[i] = bars[i].Close;
        return ys;
    }
}
