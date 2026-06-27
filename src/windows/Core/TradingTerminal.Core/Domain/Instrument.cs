using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Core.Domain;

/// <summary>
/// The canonical, broker-neutral description of a tradable instrument. One row per real-world
/// instrument; per-broker symbology is held separately in <see cref="InstrumentAlias"/>. The
/// <see cref="CanonicalSymbol"/> + <see cref="AssetClass"/> + <see cref="Exchange"/> triple is
/// the natural key (a uniqueness constraint backs it in storage).
/// </summary>
public sealed record Instrument(
    InstrumentId Id,
    string CanonicalSymbol,
    AssetClass AssetClass,
    string Exchange,
    string Currency,
    double TickSize,
    double Multiplier)
{
    /// <summary>Builds an as-yet-unpersisted instrument (id = None) for the registry to insert.</summary>
    public static Instrument New(
        string canonicalSymbol,
        AssetClass assetClass,
        string exchange = "",
        string currency = "USD",
        double tickSize = 0.01,
        double multiplier = 1.0) =>
        new(InstrumentId.None, canonicalSymbol, assetClass, exchange, currency, tickSize, multiplier);
}

/// <summary>
/// Maps one broker's symbology to a canonical <see cref="InstrumentId"/>. <see cref="BrokerSymbol"/>
/// is the string a broker's API expects (Alpaca ticker, cTrader symbol name, IB local symbol);
/// <see cref="BrokerNativeId"/> is the broker's own numeric handle when it has one (IB conId,
/// cTrader symbolId) and is null otherwise. <c>(Broker, BrokerSymbol)</c> is unique.
/// </summary>
public sealed record InstrumentAlias(
    InstrumentId InstrumentId,
    BrokerKind Broker,
    string BrokerSymbol,
    string? BrokerNativeId);
