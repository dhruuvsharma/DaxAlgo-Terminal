using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Provider-specific persistence for the instrument registry (SQLite or Postgres). The
/// <see cref="InstrumentRegistry"/> layers the in-memory cache and resolution logic on top, so
/// only the storage dialect differs between backends.
/// </summary>
internal interface IInstrumentPersistence : IDisposable
{
    void EnsureSchema();

    IReadOnlyList<Instrument> LoadInstruments();

    IReadOnlyList<(BrokerKind Broker, string Symbol, InstrumentId Id)> LoadAliases();

    /// <summary>Insert (or fetch existing) by the canonical key, returning the row id.</summary>
    InstrumentId UpsertInstrument(Instrument instrument);

    void UpsertAlias(InstrumentAlias alias);
}
