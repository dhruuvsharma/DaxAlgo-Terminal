namespace TradingTerminal.Core.Ml;

/// <summary>
/// López de Prado (2018) "Advances in Financial Machine Learning" — triple-barrier
/// labelling. Each candidate entry bar gets a label by simulating a hypothetical trade
/// forward in time:
///   +1 if the upper barrier (take-profit) is touched first
///   -1 if the lower barrier (stop-loss) is touched first
///    0 if the timeout (vertical barrier) is reached before either price barrier
///
/// Barriers are expressed in PRICE units around the entry; supply them in whatever
/// units your data uses (typically a multiple of realised vol, e.g. 2σ). The timeout is
/// in bar count.
///
/// This is the foundational labelling step every quant-ML pipeline starts with — the
/// labels are what supervised models (random forests, gradient boosting, ridge) actually
/// fit against. Anything that's not first-touch-aware is leaking information and not a
/// real label.
/// </summary>
public static class TripleBarrierLabeler
{
    public enum Label { Negative = -1, Neutral = 0, Positive = 1 }

    public sealed record LabelledBar<TBar>(int Index, TBar Bar, Label Label, int BarsToOutcome);

    /// <summary>
    /// Walk each candidate entry forward, find the first barrier touched. Barriers are
    /// PRICE differences from the entry close (positive for both — the upper is added,
    /// the lower is subtracted). Returns one label per input bar; the last
    /// <paramref name="timeoutBars"/> bars are necessarily Neutral because we don't have
    /// forward data to evaluate.
    /// </summary>
    public static IReadOnlyList<LabelledBar<TBar>> Apply<TBar>(
        IReadOnlyList<TBar> bars,
        Func<TBar, double> close,
        Func<TBar, double> high,
        Func<TBar, double> low,
        double upperBarrier,
        double lowerBarrier,
        int timeoutBars)
    {
        if (upperBarrier <= 0) throw new ArgumentOutOfRangeException(nameof(upperBarrier));
        if (lowerBarrier <= 0) throw new ArgumentOutOfRangeException(nameof(lowerBarrier));
        if (timeoutBars <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutBars));

        var labels = new List<LabelledBar<TBar>>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            var entry = close(bars[i]);
            var up = entry + upperBarrier;
            var dn = entry - lowerBarrier;
            Label label = Label.Neutral;
            int barsToOutcome = 0;

            for (var k = 1; k <= timeoutBars && i + k < bars.Count; k++)
            {
                var hi = high(bars[i + k]);
                var lo = low(bars[i + k]);

                var hitUp = hi >= up;
                var hitDn = lo <= dn;

                if (hitUp && hitDn)
                {
                    // Ambiguous within one bar — resolve conservatively to Neutral.
                    label = Label.Neutral;
                    barsToOutcome = k;
                    break;
                }
                if (hitUp) { label = Label.Positive; barsToOutcome = k; break; }
                if (hitDn) { label = Label.Negative; barsToOutcome = k; break; }
            }

            labels.Add(new LabelledBar<TBar>(i, bars[i], label, barsToOutcome));
        }

        return labels;
    }
}
