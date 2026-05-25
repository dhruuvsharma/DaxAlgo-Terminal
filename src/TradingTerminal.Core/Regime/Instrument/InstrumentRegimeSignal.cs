namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// The eight sub-signals that feed the per-instrument regime composite. The first five are
/// pure-bar signals (work on any broker that yields OHLCV); the last three need a live
/// <c>DepthSnapshot</c> and are skipped when the active broker doesn't expose L2.
/// </summary>
public enum InstrumentRegimeSignal
{
    /// <summary>Close vs N-period SMA — bullish above, bearish below.</summary>
    Trend,

    /// <summary>Rate-of-change over N bars — momentum sign.</summary>
    Momentum,

    /// <summary>Wilder RSI(14) re-centred at 50 — bullish above, bearish below.</summary>
    Strength,

    /// <summary>Z-score of close vs short SMA — overextended = mean-reversion contrarian signal.</summary>
    MeanReversion,

    /// <summary>Last-bar volume z-score signed by the bar's body direction.</summary>
    Volume,

    /// <summary>Cumulative depth imbalance across all visible levels (L2 only).</summary>
    CumulativeImbalance,

    /// <summary>Order-book imbalance over the top 3 levels (L2 only).</summary>
    ObiShallow,

    /// <summary>Order-book imbalance over up to 10 levels (L2 only).</summary>
    ObiDeep,
}
