using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.OrderFlowCube;

/// <summary>
/// L1 tick-rule synthesizer that derives <see cref="TradePrint"/> events from a stream of
/// <see cref="Quote"/>s when the broker exposes no real trade tape (cTrader, NT8 AT Interface,
/// Alpaca-unwired). Mid ticks up ⇒ Buy print at the ask, size = AskSize; mid ticks down ⇒ Sell
/// print at the bid, size = BidSize; mid unchanged ⇒ no event. Same pattern
/// a VPIN-style approximation uses. Degraded vs a real trade
/// tape but preserves the directional-flow signal.
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
