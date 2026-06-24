using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.CumulativeDelta;

/// <summary>
/// Minimal indicator helpers needed by the strategy. ATR uses Wilder smoothing (matches
/// MT5's iATR with default settings); EMA uses the standard 2/(n+1) coefficient.
/// </summary>
internal static class Indicators
{
    /// <summary>
    /// Wilder ATR over <paramref name="period"/> closing bars. Returns NaN if not enough bars.
    /// </summary>
    public static double Atr(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < period + 1) return double.NaN;

        // True Range for each bar starting from index 1 (needs prevClose).
        // Seed: simple average of first `period` TRs. Then Wilder smoothing.
        double atr = 0;
        for (var i = 1; i <= period; i++)
            atr += TrueRange(bars[i], bars[i - 1].Close);
        atr /= period;

        for (var i = period + 1; i < bars.Count; i++)
        {
            var tr = TrueRange(bars[i], bars[i - 1].Close);
            atr = ((atr * (period - 1)) + tr) / period;
        }
        return atr;
    }

    /// <summary>
    /// EMA over the closes of <paramref name="bars"/>. Seed = SMA of first <paramref name="period"/>.
    /// Returns NaN if not enough bars.
    /// </summary>
    public static double Ema(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < period) return double.NaN;

        double ema = 0;
        for (var i = 0; i < period; i++) ema += bars[i].Close;
        ema /= period;

        var k = 2.0 / (period + 1);
        for (var i = period; i < bars.Count; i++)
            ema = (bars[i].Close - ema) * k + ema;

        return ema;
    }

    /// <summary>
    /// Latest Wilder ADX over <paramref name="period"/>. Mirrors cAlgo's
    /// <c>Indicators.DirectionalMovementSystem(...).ADX</c> — same Wilder-smoothed
    /// +DI / -DI / DX → ADX chain. Returns NaN if not enough bars (need 2·period+1).
    /// </summary>
    public static double Adx(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < (2 * period) + 1) return double.NaN;

        // Seed sums for i = 1..period.
        double trSum = 0, plusDmSum = 0, minusDmSum = 0;
        for (var i = 1; i <= period; i++)
            AccumulateDirectional(bars, i, ref trSum, ref plusDmSum, ref minusDmSum);

        double smoothedTr = trSum, smoothedPlus = plusDmSum, smoothedMinus = minusDmSum;

        // First DX after the seed window. Then Wilder-smooth +DM/-DM/TR and seed ADX
        // at index 2·period using a simple average of the first `period` DX values.
        var dxValues = new double[period];
        var dxIdx = 0;
        dxValues[dxIdx++] = ComputeDx(smoothedPlus, smoothedMinus, smoothedTr);

        for (var i = period + 1; i < bars.Count; i++)
        {
            DirectionalForBar(bars, i, out var tr, out var plusDm, out var minusDm);
            smoothedTr    = smoothedTr    - (smoothedTr    / period) + tr;
            smoothedPlus  = smoothedPlus  - (smoothedPlus  / period) + plusDm;
            smoothedMinus = smoothedMinus - (smoothedMinus / period) + minusDm;

            var dx = ComputeDx(smoothedPlus, smoothedMinus, smoothedTr);

            if (dxIdx < period)
            {
                dxValues[dxIdx++] = dx;
                if (dxIdx == period)
                {
                    var seed = 0.0;
                    foreach (var v in dxValues) seed += v;
                    return WilderAdx(seed / period, bars, i, period, smoothedTr, smoothedPlus, smoothedMinus);
                }
            }
        }
        return double.NaN;
    }

    private static double WilderAdx(double adx, IReadOnlyList<Bar> bars, int seededAt, int period,
                                    double smoothedTr, double smoothedPlus, double smoothedMinus)
    {
        for (var i = seededAt + 1; i < bars.Count; i++)
        {
            DirectionalForBar(bars, i, out var tr, out var plusDm, out var minusDm);
            smoothedTr    = smoothedTr    - (smoothedTr    / period) + tr;
            smoothedPlus  = smoothedPlus  - (smoothedPlus  / period) + plusDm;
            smoothedMinus = smoothedMinus - (smoothedMinus / period) + minusDm;
            var dx = ComputeDx(smoothedPlus, smoothedMinus, smoothedTr);
            adx = ((adx * (period - 1)) + dx) / period;
        }
        return adx;
    }

    private static void AccumulateDirectional(IReadOnlyList<Bar> bars, int i,
                                              ref double trSum, ref double plusDmSum, ref double minusDmSum)
    {
        DirectionalForBar(bars, i, out var tr, out var plusDm, out var minusDm);
        trSum += tr;
        plusDmSum += plusDm;
        minusDmSum += minusDm;
    }

    private static void DirectionalForBar(IReadOnlyList<Bar> bars, int i,
                                          out double tr, out double plusDm, out double minusDm)
    {
        var cur = bars[i];
        var prev = bars[i - 1];
        var upMove = cur.High - prev.High;
        var downMove = prev.Low - cur.Low;
        plusDm  = upMove   > downMove && upMove   > 0 ? upMove   : 0;
        minusDm = downMove > upMove   && downMove > 0 ? downMove : 0;
        tr = TrueRange(cur, prev.Close);
    }

    private static double ComputeDx(double plus, double minus, double tr)
    {
        if (tr <= 0) return 0;
        var plusDi  = 100.0 * plus  / tr;
        var minusDi = 100.0 * minus / tr;
        var sum = plusDi + minusDi;
        return sum <= 0 ? 0 : 100.0 * Math.Abs(plusDi - minusDi) / sum;
    }

    private static double TrueRange(Bar bar, double prevClose)
    {
        var hl = bar.High - bar.Low;
        var hc = Math.Abs(bar.High - prevClose);
        var lc = Math.Abs(bar.Low - prevClose);
        return Math.Max(hl, Math.Max(hc, lc));
    }
}
