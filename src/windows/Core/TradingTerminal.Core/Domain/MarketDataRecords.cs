using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Core.Domain;

/// <summary>Which side initiated a trade print, when the broker reports it.</summary>
public enum AggressorSide
{
    Unknown = 0,
    Buy,
    Sell,
}

/// <summary>
/// A normalized L1 quote. Unlike the legacy broker-facing <see cref="Tick"/> (which conflates
/// arrival time with event time and zero-fills sizes), every canonical record carries both
/// timestamps and its provenance:
/// <list type="bullet">
/// <item><see cref="EventTimeUtc"/> — the exchange/broker event time when available.</item>
/// <item><see cref="IngestTimeUtc"/> — our clock at ingest. For brokers that only report
/// arrival time, the ingest layer sets <see cref="EventTimeApproximate"/> = true and copies
/// the ingest time into the event time, so consumers know the timestamp is not authoritative.</item>
/// <item><see cref="Source"/> — which broker produced it.</item>
/// <item><see cref="Sequence"/> — a per-instrument monotonic counter assigned at ingest, for
/// deterministic ordering and replay.</item>
/// </list>
/// </summary>
public sealed record Quote(
    InstrumentId InstrumentId,
    DateTime EventTimeUtc,
    DateTime IngestTimeUtc,
    double Bid,
    double Ask,
    long BidSize,
    long AskSize,
    BrokerKind Source,
    long Sequence,
    bool EventTimeApproximate)
{
    public double Mid => (Bid + Ask) * 0.5;
    public double Spread => Ask - Bid;
}

/// <summary>
/// A normalized trade print (last). Fills the gap the quote-only <see cref="Tick"/> left — a
/// broker that publishes trades (Alpaca) populates this; quote-only feeds simply never emit it.
/// </summary>
public sealed record TradePrint(
    InstrumentId InstrumentId,
    DateTime EventTimeUtc,
    DateTime IngestTimeUtc,
    double Price,
    long Size,
    AggressorSide Aggressor,
    BrokerKind Source,
    long Sequence,
    bool EventTimeApproximate);

/// <summary>
/// A normalized OHLCV bar carrying canonical identity and provenance. <see cref="IsFinal"/> is
/// false for an in-progress streaming bar (so the store can upsert the same
/// <c>(InstrumentId, Size, OpenTimeUtc)</c> row as it updates) and true once the bar closes.
/// </summary>
public sealed record OhlcvBar(
    InstrumentId InstrumentId,
    BarSize Size,
    DateTime OpenTimeUtc,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    BrokerKind Source,
    bool IsFinal)
{
    /// <summary>Adapts a legacy broker-facing <see cref="Bar"/> into a canonical bar.</summary>
    public static OhlcvBar FromBar(Bar bar, InstrumentId id, BarSize size, BrokerKind source, bool isFinal) =>
        new(id, size, bar.TimestampUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, source, isFinal);

    /// <summary>Projects back to the legacy <see cref="Bar"/> shape for existing chart/strategy code.</summary>
    public Bar ToBar() => new(OpenTimeUtc, Open, High, Low, Close, Volume);
}
