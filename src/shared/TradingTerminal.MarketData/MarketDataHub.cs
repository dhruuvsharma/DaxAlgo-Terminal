using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// In-memory <see cref="IMarketDataHub"/> built on Rx subjects, one per instrument/stream. The
/// ingest layer publishes canonical records here; subscribers attach by <see cref="InstrumentId"/>.
/// Subjects are created lazily and cached, so subscribing before the first publish still wires
/// up (the ingest service starts the underlying broker feed separately). Hot/multicast — a fast
/// in-process bus, no persistence on this path.
/// </summary>
internal sealed class MarketDataHub : IMarketDataHub
{
    private readonly ConcurrentDictionary<int, Subject<Quote>> _quotes = new();
    private readonly ConcurrentDictionary<int, Subject<TradePrint>> _trades = new();
    private readonly ConcurrentDictionary<(int, BarSize), Subject<OhlcvBar>> _bars = new();
    private readonly ConcurrentDictionary<int, Subject<DepthSnapshot>> _depth = new();

    public IObservable<Quote> Quotes(InstrumentId instrumentId) =>
        _quotes.GetOrAdd(instrumentId.Value, _ => new Subject<Quote>()).AsObservable();

    public IObservable<TradePrint> Trades(InstrumentId instrumentId) =>
        _trades.GetOrAdd(instrumentId.Value, _ => new Subject<TradePrint>()).AsObservable();

    public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) =>
        _bars.GetOrAdd((instrumentId.Value, size), _ => new Subject<OhlcvBar>()).AsObservable();

    public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) =>
        _depth.GetOrAdd(instrumentId.Value, _ => new Subject<DepthSnapshot>()).AsObservable();

    public void PublishQuote(Quote quote) =>
        _quotes.GetOrAdd(quote.InstrumentId.Value, _ => new Subject<Quote>()).OnNext(quote);

    public void PublishTrade(TradePrint trade) =>
        _trades.GetOrAdd(trade.InstrumentId.Value, _ => new Subject<TradePrint>()).OnNext(trade);

    public void PublishBar(OhlcvBar bar) =>
        _bars.GetOrAdd((bar.InstrumentId.Value, bar.Size), _ => new Subject<OhlcvBar>()).OnNext(bar);

    public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) =>
        _depth.GetOrAdd(instrumentId.Value, _ => new Subject<DepthSnapshot>()).OnNext(snapshot);
}
