using TradingTerminal.Core.Domain;

namespace DaxAlgo.Blocks;

/// <summary>Live quotes, trades, bars and order-book depth for any number of instruments.</summary>
[BlockCard(
    "market",
    "live quotes, trades, bars and depth for any instruments",
    Does = "Subscribes to live market data per instrument and keeps a short recent window of each stream you subscribed to.",
    Needs = "An InstrumentId — usually from an Instrument setting, or from the instruments block. Subscribe to every instrument the unit uses; there is no limit on how many.",
    Limits = "Handlers run one at a time on the unit's thread; keep them short. Recent windows hold the last 512 items per stream and start filling when you subscribe — use the history block for anything earlier. OnBar delivers forming bars too: check IsFinal.",
    Types = [typeof(Quote), typeof(TradePrint), typeof(OhlcvBar), typeof(DepthSnapshot), typeof(DepthLevel), typeof(BarSize), typeof(AggressorSide)],
    Order = 20)]
public interface IMarketData
{
    /// <summary>Calls <paramref name="handler"/> for every top-of-book quote; dispose to unsubscribe.</summary>
    IDisposable OnQuote(InstrumentId instrument, Action<Quote> handler);

    /// <summary>Calls <paramref name="handler"/> for every print on the trade tape; dispose to unsubscribe.</summary>
    IDisposable OnTrade(InstrumentId instrument, Action<TradePrint> handler);

    /// <summary>Calls <paramref name="handler"/> for every bar update of one size, forming and final; dispose to unsubscribe.</summary>
    IDisposable OnBar(InstrumentId instrument, BarSize size, Action<OhlcvBar> handler);

    /// <summary>Calls <paramref name="handler"/> for every order-book snapshot; dispose to unsubscribe.</summary>
    IDisposable OnDepth(InstrumentId instrument, Action<DepthSnapshot> handler);

    /// <summary>The most recent bars of one size, oldest first.</summary>
    IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int count);

    /// <summary>The most recent quotes, oldest first.</summary>
    IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int count);

    /// <summary>The most recent trade prints, oldest first.</summary>
    IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int count);

    /// <summary>The latest order-book snapshot, or null before the first one arrives.</summary>
    DepthSnapshot? LatestDepth(InstrumentId instrument);
}
