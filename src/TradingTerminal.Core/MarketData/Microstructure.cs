using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Stateless microstructure helpers. All functions are pure: same inputs → same outputs,
/// no allocations except where noted. Used by HFT-flavoured strategies; live in Core so
/// they don't accidentally pull in WPF / broker dependencies.
/// </summary>
public static class Microstructure
{
    /// <summary>
    /// Size-weighted "fair price" between bid and ask. The intuition: the side with smaller
    /// resting size is the side likely to move first, so the fair price leans toward the
    /// thinner side. Formally: <c>(bidSize * ask + askSize * bid) / (bidSize + askSize)</c>.
    /// Falls back to the simple mid when either size is zero or unknown.
    /// </summary>
    public static double Microprice(double bid, double ask, long bidSize, long askSize)
    {
        var total = bidSize + askSize;
        if (total <= 0) return (bid + ask) * 0.5;
        return ((double)bidSize * ask + (double)askSize * bid) / total;
    }

    /// <inheritdoc cref="Microprice(double,double,long,long)"/>
    public static double Microprice(Tick t) => Microprice(t.Bid, t.Ask, t.BidSize, t.AskSize);

    /// <summary>
    /// Queue imbalance in [-1, 1]: <c>(bidSize - askSize) / (bidSize + askSize)</c>.
    /// Positive ⇒ more depth on the bid (buying pressure); negative ⇒ more on the ask.
    /// </summary>
    public static double QueueImbalance(long bidSize, long askSize)
    {
        var total = bidSize + askSize;
        if (total <= 0) return 0;
        return (double)(bidSize - askSize) / total;
    }

    /// <inheritdoc cref="QueueImbalance(long,long)"/>
    public static double QueueImbalance(Tick t) => QueueImbalance(t.BidSize, t.AskSize);

    /// <summary>Half-spread in price units: <c>(ask - bid) / 2</c>.</summary>
    public static double HalfSpread(double bid, double ask) => (ask - bid) * 0.5;

    /// <summary>Half-spread in price units for a tick.</summary>
    public static double HalfSpread(Tick t) => HalfSpread(t.Bid, t.Ask);
}
