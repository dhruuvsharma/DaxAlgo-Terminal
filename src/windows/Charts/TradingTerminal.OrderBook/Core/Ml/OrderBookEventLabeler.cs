namespace TradingTerminal.Core.Ml;

/// <summary>
/// The label rules for the order-book event targets — pure, tick-aware threshold predicates so
/// the definitions are testable in isolation and identical between learning and any offline
/// analysis. All "future" aggregates are taken over the event window W by the caller.
/// </summary>
public static class OrderBookEventLabeler
{
    private const double Epsilon = 1e-12;

    /// <summary>Spread widened: the max spread observed over the window reached at least
    /// <paramref name="widenTicks"/> ticks above the reference spread. An absolute tick threshold
    /// (not multiplicative) so the rule stays meaningful on books pinned at a 1-tick spread.</summary>
    public static bool SpreadWidened(double referenceSpread, double maxFutureSpread, double tick, double widenTicks = 1.0)
        => maxFutureSpread >= referenceSpread + widenTicks * tick - Epsilon;

    /// <summary>Depth drained: either side's top-3 resting depth dipped to at most
    /// <paramref name="drainRatio"/> of its reference value at any step in the window. Worst-side
    /// semantics — one label covers evaporation on whichever side thins first.</summary>
    public static bool DepthDrained(long referenceBid3, long referenceAsk3, long minFutureBid3, long minFutureAsk3, double drainRatio = 0.7)
        => (referenceBid3 > 0 && minFutureBid3 <= referenceBid3 * drainRatio + Epsilon)
        || (referenceAsk3 > 0 && minFutureAsk3 <= referenceAsk3 * drainRatio + Epsilon);

    /// <summary>Sweep cost jumped: the worst-side sweep cost reached at least
    /// <paramref name="jumpRatio"/> × the reference cost, with a one-tick floor on the reference so
    /// a sweep that fills at the touch (cost 0) still has a meaningful threshold.</summary>
    public static bool SweepJumped(double referenceWorstSweep, double maxFutureWorstSweep, double tick, double jumpRatio = 1.25)
        => maxFutureWorstSweep >= jumpRatio * Math.Max(referenceWorstSweep, tick) - Epsilon;
}
