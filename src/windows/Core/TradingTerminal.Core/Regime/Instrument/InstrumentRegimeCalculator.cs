using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// Pure function — no I/O, no clocks. Maps <see cref="InstrumentRegimeInputs"/> to a signed
/// composite snapshot using eight sub-signals: five derived from OHLCV bars (Trend, Momentum,
/// Strength, MeanReversion, Volume) and three from the optional <c>DepthSnapshot</c>
/// (CumulativeImbalance, ObiShallow, ObiDeep). Missing inputs drop their signal cleanly
/// (Valid=false, weight redistributed) rather than failing the whole composite.
/// </summary>
public static class InstrumentRegimeCalculator
{
    // ── Tunables (chosen so a typical "trending up" instrument scores around +50) ──────────
    private const int TrendPeriod = 50;          // SMA window for Trend
    private const int MomentumLookback = 10;     // bars for ROC
    private const int RsiPeriod = 14;
    private const int AtrPeriod = 14;
    private const int MeanRevPeriod = 20;        // SMA + stdev window for z-score
    private const int VolumePeriod = 20;         // mean + stdev for volume z-score
    private const int VolatilityRankLookback = 50;

    // Reference scales used to normalise each signal into [-1, +1] via tanh. These are
    // intentionally generous so the typical signal lives in the middle of its range and only
    // extreme readings saturate.
    private const double TrendAtrUnits = 2.5;    // Close needs ~2.5 ATR above SMA to score +1
    private const double MomentumPct = 0.02;     // 2 % move over MomentumLookback ⇒ score +1
    private const double VolumeZRefScale = 2.0;  // Volume z ≥ 2 ⇒ saturate

    public static InstrumentRegimeSnapshot Compute(InstrumentRegimeInputs inputs, DateTime nowUtc)
    {
        var bars = inputs.Bars;
        var n = bars.Count;
        if (n == 0)
            return InstrumentRegimeSnapshot.Empty with { Symbol = inputs.Symbol, Timeframe = inputs.Timeframe, GeneratedAtUtc = nowUtc };

        var lastClose = bars[^1].Close;

        // Indicator pre-computes — we share the loop bodies rather than calling the streaming
        // Indicators helpers per-bar, because we already have the whole window in memory.
        var sma50 = SmaTail(bars, TrendPeriod);
        var atr14 = AtrTail(bars, AtrPeriod);
        var atrPercent = atr14 is double a && lastClose > 0 ? a / lastClose * 100 : (double?)null;
        var volatilityRank = VolatilityPercentileRank(bars, AtrPeriod, VolatilityRankLookback);

        var signals = new List<InstrumentSignalScore>(8)
        {
            BuildTrend(bars, sma50, atr14),
            BuildMomentum(bars),
            BuildStrength(bars),
            BuildMeanReversion(bars),
            BuildVolume(bars),
        };

        if (inputs.Depth is { } depth && (depth.Bids.Count > 0 || depth.Asks.Count > 0))
        {
            signals.Add(BuildDepthImbalance(depth, depthLevels: depth.Bids.Count + depth.Asks.Count, signal: InstrumentRegimeSignal.CumulativeImbalance, weight: 0.15));
            signals.Add(BuildDepthImbalance(depth, depthLevels: 3,  signal: InstrumentRegimeSignal.ObiShallow, weight: 0.10));
            signals.Add(BuildDepthImbalance(depth, depthLevels: 10, signal: InstrumentRegimeSignal.ObiDeep,    weight: 0.10));
        }
        else
        {
            signals.Add(new InstrumentSignalScore(InstrumentRegimeSignal.CumulativeImbalance, 0, 0.15, 0, false, "depth unavailable"));
            signals.Add(new InstrumentSignalScore(InstrumentRegimeSignal.ObiShallow,         0, 0.10, 0, false, "depth unavailable"));
            signals.Add(new InstrumentSignalScore(InstrumentRegimeSignal.ObiDeep,            0, 0.10, 0, false, "depth unavailable"));
        }

        // Renormalise weights of valid signals so the composite always uses the same scale
        // regardless of how many sub-signals were available.
        double validWeight = 0;
        foreach (var s in signals) if (s.Valid) validWeight += s.Weight;

        var final = new List<InstrumentSignalScore>(signals.Count);
        double composite = 0;
        if (validWeight <= 1e-9)
        {
            // All signals invalid — composite is 0 with everything frozen as-is.
            final.AddRange(signals);
        }
        else
        {
            foreach (var s in signals)
            {
                if (!s.Valid) { final.Add(s); continue; }
                var normalisedWeight = s.Weight / validWeight;
                var contribution = s.Score * normalisedWeight * 100; // -100..+100 scale
                composite += contribution;
                final.Add(s with { Contribution = contribution });
            }
        }

        composite = Math.Clamp(composite, -100.0, 100.0);
        var band = InstrumentRegimeBandExtensions.FromScore(composite);

        return new InstrumentRegimeSnapshot(
            Symbol: inputs.Symbol,
            Timeframe: inputs.Timeframe,
            CompositeScore: composite,
            Band: band,
            Signals: final,
            LastClose: lastClose,
            AtrPercent: atrPercent,
            VolatilityRank: volatilityRank,
            BarCount: n,
            DepthLevels: inputs.Depth is { } d ? d.Bids.Count + d.Asks.Count : 0,
            GeneratedAtUtc: nowUtc,
            Unavailable: false);
    }

    // ── Bar-based signals ──────────────────────────────────────────────────────────────────

    private static InstrumentSignalScore BuildTrend(IReadOnlyList<Bar> bars, double? sma, double? atr)
    {
        if (sma is not double s || atr is not double a || a <= 0)
            return new(InstrumentRegimeSignal.Trend, 0, 0.20, 0, false, "warming up");
        var diff = bars[^1].Close - s;
        var atrUnits = diff / a;
        var score = Math.Tanh(atrUnits / TrendAtrUnits);
        var detail = $"close {bars[^1].Close:F4} vs SMA{TrendPeriod} {s:F4} ({atrUnits:+0.00;-0.00;0.00}σ ATR)";
        return new(InstrumentRegimeSignal.Trend, score, 0.20, 0, true, detail);
    }

    private static InstrumentSignalScore BuildMomentum(IReadOnlyList<Bar> bars)
    {
        if (bars.Count <= MomentumLookback)
            return new(InstrumentRegimeSignal.Momentum, 0, 0.15, 0, false, "warming up");
        var thenIdx = bars.Count - 1 - MomentumLookback;
        var then = bars[thenIdx].Close;
        if (then <= 0)
            return new(InstrumentRegimeSignal.Momentum, 0, 0.15, 0, false, "invalid base");
        var roc = (bars[^1].Close - then) / then;
        var score = Math.Tanh(roc / MomentumPct);
        return new(InstrumentRegimeSignal.Momentum, score, 0.15, 0, true, $"ROC{MomentumLookback} {roc:+0.00%;-0.00%;0.00%}");
    }

    private static InstrumentSignalScore BuildStrength(IReadOnlyList<Bar> bars)
    {
        if (bars.Count <= RsiPeriod + 1)
            return new(InstrumentRegimeSignal.Strength, 0, 0.15, 0, false, "warming up");
        var rsi = WilderRsi(bars, RsiPeriod);
        if (rsi is null)
            return new(InstrumentRegimeSignal.Strength, 0, 0.15, 0, false, "warming up");
        var score = (rsi.Value - 50.0) / 50.0;
        return new(InstrumentRegimeSignal.Strength, score, 0.15, 0, true, $"RSI {rsi.Value:F1}");
    }

    private static InstrumentSignalScore BuildMeanReversion(IReadOnlyList<Bar> bars)
    {
        if (bars.Count < MeanRevPeriod + 1)
            return new(InstrumentRegimeSignal.MeanReversion, 0, 0.10, 0, false, "warming up");
        var (mean, stdev) = MeanStdevTail(bars, MeanRevPeriod);
        if (stdev <= 1e-9)
            return new(InstrumentRegimeSignal.MeanReversion, 0, 0.10, 0, false, "flat window");
        var z = (bars[^1].Close - mean) / stdev;
        // Overextended = mean-reversion bearish (contrarian) ⇒ flip sign.
        var score = -Math.Tanh(z / 2.0);
        return new(InstrumentRegimeSignal.MeanReversion, score, 0.10, 0, true, $"z {z:+0.00;-0.00;0.00} of MA{MeanRevPeriod}");
    }

    private static InstrumentSignalScore BuildVolume(IReadOnlyList<Bar> bars)
    {
        if (bars.Count < VolumePeriod + 1)
            return new(InstrumentRegimeSignal.Volume, 0, 0.10, 0, false, "warming up");
        var window = bars.Count - 1;
        double sum = 0, sumSq = 0;
        for (var i = window - VolumePeriod; i < window; i++) { sum += bars[i].Volume; sumSq += (double)bars[i].Volume * bars[i].Volume; }
        var mean = sum / VolumePeriod;
        var variance = sumSq / VolumePeriod - mean * mean;
        var stdev = variance > 0 ? Math.Sqrt(variance) : 0;
        if (stdev <= 1e-9 || mean <= 0)
            return new(InstrumentRegimeSignal.Volume, 0, 0.10, 0, false, "no volume data");
        var z = (bars[^1].Volume - mean) / stdev;
        var bodySign = bars[^1].Close >= bars[^1].Open ? 1 : -1;
        var score = Math.Tanh(z / VolumeZRefScale) * bodySign;
        return new(InstrumentRegimeSignal.Volume, score, 0.10, 0, true, $"vol z {z:+0.00;-0.00;0.00} ({(bodySign > 0 ? "up" : "down")} bar)");
    }

    // ── L2 signals ─────────────────────────────────────────────────────────────────────────

    private static InstrumentSignalScore BuildDepthImbalance(DepthSnapshot depth, int depthLevels, InstrumentRegimeSignal signal, double weight)
    {
        var ci = Microstructure.CumulativeImbalance(depth, depthLevels);
        // Already in [-1, +1] — heavier bid is positive (bullish).
        return new(signal, ci, weight, 0, true, $"CI@{depthLevels} {ci:+0.00;-0.00;0.00}");
    }

    // ── Indicator helpers (local — operate on the whole window we already have in memory) ──

    private static double? SmaTail(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < period) return null;
        double sum = 0;
        for (var i = bars.Count - period; i < bars.Count; i++) sum += bars[i].Close;
        return sum / period;
    }

    private static double? AtrTail(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < period + 1) return null;
        // Wilder ATR seeded with simple average of TR over the first `period` true ranges,
        // then smoothed across the rest of the window.
        double sumSeed = 0;
        for (var i = 1; i <= period; i++) sumSeed += TrueRange(bars[i - 1], bars[i]);
        var atr = sumSeed / period;
        for (var i = period + 1; i < bars.Count; i++)
        {
            var tr = TrueRange(bars[i - 1], bars[i]);
            atr = (atr * (period - 1) + tr) / period;
        }
        return atr;
    }

    private static double TrueRange(Bar prev, Bar cur)
    {
        var hl = cur.High - cur.Low;
        var hc = Math.Abs(cur.High - prev.Close);
        var lc = Math.Abs(cur.Low - prev.Close);
        return Math.Max(hl, Math.Max(hc, lc));
    }

    private static double? WilderRsi(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < period + 1) return null;
        double avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= period; i++)
        {
            var diff = bars[i].Close - bars[i - 1].Close;
            if (diff > 0) avgGain += diff; else avgLoss -= diff;
        }
        avgGain /= period; avgLoss /= period;
        for (var i = period + 1; i < bars.Count; i++)
        {
            var diff = bars[i].Close - bars[i - 1].Close;
            var g = diff > 0 ? diff : 0;
            var l = diff < 0 ? -diff : 0;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
        }
        if (avgLoss <= 1e-12) return avgGain > 0 ? 100.0 : 50.0;
        var rs = avgGain / avgLoss;
        return 100.0 - 100.0 / (1.0 + rs);
    }

    private static (double Mean, double Stdev) MeanStdevTail(IReadOnlyList<Bar> bars, int period)
    {
        double sum = 0, sumSq = 0;
        for (var i = bars.Count - period; i < bars.Count; i++) { sum += bars[i].Close; sumSq += bars[i].Close * bars[i].Close; }
        var mean = sum / period;
        var variance = sumSq / period - mean * mean;
        return (mean, variance > 0 ? Math.Sqrt(variance) : 0);
    }

    /// <summary>Percentile rank of the current ATR within the trailing <paramref name="lookback"/>
    /// per-bar ATRs (re-computed Wilder-style for each window slice). Returns a percentile in
    /// <c>[0, 100]</c> or null if the window is too short.</summary>
    private static double? VolatilityPercentileRank(IReadOnlyList<Bar> bars, int atrPeriod, int lookback)
    {
        if (bars.Count < atrPeriod + lookback) return null;
        var atrSeries = new double[lookback];
        for (var k = 0; k < lookback; k++)
        {
            var end = bars.Count - lookback + k + 1;
            var slice = new ArraySlice(bars, end - atrPeriod - 1, atrPeriod + 1);
            atrSeries[k] = AtrOfSlice(slice, atrPeriod);
        }
        var current = atrSeries[^1];
        var below = 0;
        for (var i = 0; i < atrSeries.Length; i++) if (atrSeries[i] < current) below++;
        return 100.0 * below / atrSeries.Length;
    }

    private static double AtrOfSlice(ArraySlice slice, int period)
    {
        double sum = 0;
        for (var i = 1; i <= period; i++) sum += TrueRange(slice[i - 1], slice[i]);
        return sum / period;
    }

    private readonly struct ArraySlice
    {
        private readonly IReadOnlyList<Bar> _source;
        private readonly int _offset;
        public int Count { get; }
        public ArraySlice(IReadOnlyList<Bar> source, int offset, int count)
        {
            _source = source; _offset = offset; Count = count;
        }
        public Bar this[int index] => _source[_offset + index];
    }
}
