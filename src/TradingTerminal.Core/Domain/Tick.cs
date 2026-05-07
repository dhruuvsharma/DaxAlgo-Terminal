namespace TradingTerminal.Core.Domain;

/// <summary>
/// A single bid/ask quote update from IB's tick-by-tick BidAsk feed. Sizes are best-effort
/// and may be zero on instruments where IB doesn't publish them. Trade prints / volume are
/// not represented here — this is a quote feed, not a trade feed.
/// </summary>
public sealed record Tick(
    DateTime TimestampUtc,
    double Bid,
    double Ask,
    long BidSize,
    long AskSize);
