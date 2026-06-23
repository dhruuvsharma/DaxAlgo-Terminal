using TradingTerminal.Core.Domain;

namespace TradingTerminal.Backtest.Engine;

/// <summary>Which market-data payload a <see cref="MarketEvent"/> carries.</summary>
public enum MarketEventKind
{
    Quote,
    Trade,
    Depth,
    Bar,
}

/// <summary>
/// One time-ordered market-data event flowing out of a feed and into the engine. Carries its
/// <see cref="Instrument"/> so a portfolio run can interleave many instruments in one stream and the
/// engine can route each event to the right book/position. Exactly one payload is set per the
/// <see cref="Kind"/>. This type stays inside the engine — kernels never see it; they get typed
/// callbacks (<c>OnQuoteAsync</c> etc.).
/// </summary>
public readonly record struct MarketEvent(
    DateTime TimestampUtc,
    InstrumentId Instrument,
    MarketEventKind Kind,
    Tick? Quote = null,
    TradePrint? Trade = null,
    DepthSnapshot? Depth = null,
    OhlcvBar? Bar = null)
{
    public static MarketEvent OfQuote(InstrumentId id, Tick quote) =>
        new(quote.TimestampUtc, id, MarketEventKind.Quote, Quote: quote);

    public static MarketEvent OfTrade(InstrumentId id, TradePrint trade) =>
        new(trade.EventTimeUtc, id, MarketEventKind.Trade, Trade: trade);

    public static MarketEvent OfDepth(InstrumentId id, DateTime tsUtc, DepthSnapshot depth) =>
        new(tsUtc, id, MarketEventKind.Depth, Depth: depth);

    public static MarketEvent OfBar(InstrumentId id, OhlcvBar bar) =>
        new(bar.OpenTimeUtc, id, MarketEventKind.Bar, Bar: bar);
}
