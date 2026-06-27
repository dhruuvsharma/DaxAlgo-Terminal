using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

/// <summary>
/// L1 tick-rule synthesizer that derives <see cref="TradePrint"/> events from a stream of
/// <see cref="Quote"/>s when the broker exposes no real trade tape (cTrader, NT8 AT Interface,
/// Alpaca-unwired). For each quote update:
/// <list type="bullet">
/// <item>Mid ticks up ⇒ synthesize a Buy print at the ask, size = AskSize (top-of-book on the
/// side that just got hit).</item>
/// <item>Mid ticks down ⇒ synthesize a Sell print at the bid, size = BidSize.</item>
/// <item>Mid unchanged ⇒ no event.</item>
/// </list>
/// This is the textbook L1 fallback; the same pattern <c>OrderFlowToxicityStrategy</c> uses for
/// its VPIN approximation. Real trade volume (what actually filled) is unknowable from L1, so we
/// substitute top-of-book size as a directional-flow proxy. Signal quality is degraded vs a
/// real trade tape but the regime-detection edge survives.
/// </summary>
internal sealed class QuoteDerivedTradeSynthesizer
{
    private Quote? _prev;

    public TradePrint? Synthesize(Quote q)
    {
        var prev = _prev;
        _prev = q;
        if (prev is null) return null;
        if (q.Mid == prev.Mid) return null;
        var isBuy = q.Mid > prev.Mid;
        var price = isBuy ? q.Ask : q.Bid;
        var size = Math.Max(1L, isBuy ? q.AskSize : q.BidSize);
        return new TradePrint(
            q.InstrumentId,
            q.EventTimeUtc,
            q.IngestTimeUtc,
            price,
            size,
            isBuy ? AggressorSide.Buy : AggressorSide.Sell,
            q.Source,
            q.Sequence,
            q.EventTimeApproximate);
    }

    public void Reset() => _prev = null;
}
