using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// Pure, side-effect-free order-flow-pressure math for one ticker / one completed 1-minute candle.
/// All inputs are passed in (no I/O, no clock) so every branch is unit-testable. The classification
/// mirrors the spec exactly: heavy relative volume that price either absorbs (Absorption) or runs
/// through (Breakthrough/Breakdown), gated by candle position, price impact and book imbalance.
/// </summary>
public static class PressureMapCalculator
{
    /// <summary>Where in the candle's range price closed: 1.0 = on the high, 0.0 = on the low,
    /// 0.5 = mid-range (volume absorbed without a directional close). 0.5 when high == low.</summary>
    public static double CandlePosition(double high, double low, double close)
        => high <= low ? 0.5 : (close - low) / (high - low);

    /// <summary>Body size relative to 1m ATR(14). 0 when ATR is unavailable (cold start) so a missing
    /// baseline never manufactures a breakthrough.</summary>
    public static double PriceImpact(double open, double close, double atr14)
        => atr14 <= 0 ? 0 : Math.Abs(close - open) / atr14;

    /// <summary>(bidDepth − askDepth) / (bidDepth + askDepth) ∈ [-1, +1]; 0 on an empty/one-sided book.
    /// +1 = strong bid support, -1 = strong ask pressure.</summary>
    public static double BookImbalance(double bidDepth, double askDepth)
    {
        var total = bidDepth + askDepth;
        return total <= 0 ? 0 : (bidDepth - askDepth) / total;
    }

    /// <summary>Simple-average True Range over the last <paramref name="period"/> bars. Needs ≥2 bars
    /// (the first has no previous close); returns 0 below that.</summary>
    public static double Atr(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < 2 || period < 1) return 0;
        var start = Math.Max(1, bars.Count - period);
        double sum = 0;
        var count = 0;
        for (var i = start; i < bars.Count; i++)
        {
            var h = bars[i].High;
            var l = bars[i].Low;
            var prevClose = bars[i - 1].Close;
            var tr = Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));
            sum += tr;
            count++;
        }
        return count > 0 ? sum / count : 0;
    }

    /// <summary>Cell brightness 0..1, banded by relative volume per the spec
    /// (&lt;1.5 dim … &gt;5 max). Drives the base→high-intensity colour blend in the view.</summary>
    public static double Intensity(double relativeVolume) => relativeVolume switch
    {
        < 1.5 => 0.15,
        < 2.0 => 0.35,
        < 3.0 => 0.60,
        < 5.0 => 0.80,
        _ => 1.0,
    };

    /// <summary>Classify the candle into one of the five pressure regimes. <paramref name="minRelVol"/>
    /// is the live "minimum relative volume" gate (default 2.0) below which nothing is a signal.
    /// Breakthrough/Breakdown (impact ≥ <see cref="OrderFlowPressureMapOptions.BreakthroughMinPriceImpact"/>)
    /// and Absorption (impact ≤ <see cref="OrderFlowPressureMapOptions.AbsorptionMaxPriceImpact"/>) are
    /// mutually exclusive on price impact, so evaluation order is immaterial.</summary>
    public static PressureSignal Classify(
        double relVol, double candlePosition, double priceImpact, double bookImbalance,
        double open, double close, double minRelVol, OrderFlowPressureMapOptions o)
    {
        if (relVol < minRelVol) return PressureSignal.Neutral;

        if (candlePosition >= o.BullishBreakthroughMinCandlePosition
            && priceImpact >= o.BreakthroughMinPriceImpact && close > open)
            return PressureSignal.BullishBreakthrough;

        if (candlePosition <= o.BearishBreakdownMaxCandlePosition
            && priceImpact >= o.BreakthroughMinPriceImpact && close < open)
            return PressureSignal.BearishBreakdown;

        if (candlePosition >= o.BullishAbsorptionMinCandlePosition
            && priceImpact <= o.AbsorptionMaxPriceImpact && bookImbalance >= o.BookImbalanceThreshold)
            return PressureSignal.BullishAbsorption;

        if (candlePosition <= o.BearishAbsorptionMaxCandlePosition
            && priceImpact <= o.AbsorptionMaxPriceImpact && bookImbalance <= -o.BookImbalanceThreshold)
            return PressureSignal.BearishAbsorption;

        return PressureSignal.Neutral;
    }

    /// <summary>Evaluate one completed candle end-to-end into an immutable <see cref="PressureCell"/>.
    /// <paramref name="baselineVolume"/> is the same-time-of-day 20-day average (or the 30-minute
    /// intraday fallback); ≤0 yields relative volume 0 (neutral) rather than a divide-by-zero.</summary>
    public static PressureCell Evaluate(
        OhlcvBar bar, double atr14, double baselineVolume,
        double bidDepth, double askDepth, double minRelVol, OrderFlowPressureMapOptions o)
    {
        var relVol = baselineVolume > 0 ? bar.Volume / baselineVolume : 0;
        var position = CandlePosition(bar.High, bar.Low, bar.Close);
        var impact = PriceImpact(bar.Open, bar.Close, atr14);
        var imbalance = BookImbalance(bidDepth, askDepth);
        var signal = Classify(relVol, position, impact, imbalance, bar.Open, bar.Close, minRelVol, o);
        return new PressureCell(
            bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
            relVol, position, impact, imbalance, signal, Intensity(relVol));
    }
}
