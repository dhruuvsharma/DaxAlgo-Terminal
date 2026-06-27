using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// The live, in-memory, broker-agnostic publish/subscribe bus for normalized market data.
/// The ingest layer publishes canonical records here as they arrive; strategies and view-models
/// subscribe by <see cref="InstrumentId"/> and receive a single uniform stream regardless of
/// which broker is connected. This is the low-latency hot path — persistence happens in parallel
/// via <see cref="IMarketDataStore"/>, off this thread.
///
/// Observables are hot and multicast: subscribing does not start a broker feed (that is the
/// ingest service's job) and multiple subscribers to the same instrument share one stream.
/// </summary>
public interface IMarketDataHub
{
    IObservable<Quote> Quotes(InstrumentId instrumentId);
    IObservable<TradePrint> Trades(InstrumentId instrumentId);
    IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size);
    IObservable<DepthSnapshot> Depth(InstrumentId instrumentId);

    // ---- Publishers (called by the ingest layer; strategies use the observables above) ----
    void PublishQuote(Quote quote);
    void PublishTrade(TradePrint trade);
    void PublishBar(OhlcvBar bar);
    void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot);
}
