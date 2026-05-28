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

/// <summary>
/// A single trade print (last) from a broker's tick-by-tick AllLast feed. Broker-shaped;
/// the ingest layer wraps these into canonical <see cref="TradePrint"/>s carrying instrument id,
/// sequence, and ingest time. <see cref="Aggressor"/> is filled when the broker reports the
/// initiating side directly; brokers that don't (IB, NT) emit <see cref="AggressorSide.Unknown"/>
/// and the ingest layer infers via the Lee-Ready quote rule against the current best bid/ask.
/// </summary>
public sealed record TradeTick(
    DateTime TimestampUtc,
    double Price,
    long Size,
    AggressorSide Aggressor);
