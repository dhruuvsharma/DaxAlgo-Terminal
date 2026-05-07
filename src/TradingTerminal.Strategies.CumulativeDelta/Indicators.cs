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

    private static double TrueRange(Bar bar, double prevClose)
    {
        var hl = bar.High - bar.Low;
        var hc = Math.Abs(bar.High - prevClose);
        var lc = Math.Abs(bar.Low - prevClose);
        return Math.Max(hl, Math.Max(hc, lc));
    }
}
