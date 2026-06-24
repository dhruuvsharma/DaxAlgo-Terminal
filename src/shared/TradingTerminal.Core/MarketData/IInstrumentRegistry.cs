using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// The canonical-identity resolver. Maps between a broker's symbology and the surrogate
/// <see cref="InstrumentId"/> the rest of the system uses. Ingest calls
/// <see cref="ResolveOrCreate(Contract, BrokerKind)"/> to turn an inbound broker contract into a
/// stable id (auto-registering on first sight); order routing calls
/// <see cref="ToBrokerSymbol"/> to go the other way. Backed by the market-data store so the
/// mapping survives restarts.
/// </summary>
public interface IInstrumentRegistry
{
    /// <summary>Look up a canonical instrument by id, or null if unknown.</summary>
    Instrument? Get(InstrumentId id);

    /// <summary>Resolve a broker symbol to its canonical id, or null if not registered.</summary>
    InstrumentId? Resolve(BrokerKind broker, string brokerSymbol);

    /// <summary>
    /// Resolve the broker contract to a canonical id, creating the instrument and alias if this
    /// is the first time we've seen it (asset class inferred from <see cref="Contract.SecType"/>).
    /// Idempotent — repeated calls for the same contract return the same id.
    /// </summary>
    InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker);

    /// <summary>The broker symbol to use when subscribing/ordering this instrument on a broker,
    /// or null when no alias is registered for that broker.</summary>
    string? ToBrokerSymbol(InstrumentId id, BrokerKind broker);

    /// <summary>Register (or update) a broker alias for an existing instrument.</summary>
    void RegisterAlias(InstrumentAlias alias);

    /// <summary>Every canonical instrument currently known.</summary>
    IReadOnlyList<Instrument> All();
}
