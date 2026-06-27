using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Provider-neutral <see cref="IInstrumentRegistry"/>: the canonical-identity cache and
/// resolution logic, backed by an <see cref="IInstrumentPersistence"/> (SQLite or Postgres).
/// Per-tick <see cref="Resolve"/> is a dictionary hit; mutations (first sight of an instrument)
/// are rare and serialized under a lock, writing through to persistence.
/// </summary>
internal sealed class InstrumentRegistry : IInstrumentRegistry, IDisposable
{
    private readonly IInstrumentPersistence _persistence;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private readonly Dictionary<int, Instrument> _byId = new();
    private readonly Dictionary<(BrokerKind, string), InstrumentId> _aliasToId = new();
    private readonly Dictionary<(string, AssetClass, string), InstrumentId> _canonicalToId = new();

    public InstrumentRegistry(IInstrumentPersistence persistence, ILogger logger)
    {
        _persistence = persistence;
        _logger = logger;
        _persistence.EnsureSchema();
        LoadCache();
    }

    public Instrument? Get(InstrumentId id)
    {
        lock (_gate) return _byId.TryGetValue(id.Value, out var ins) ? ins : null;
    }

    public InstrumentId? Resolve(BrokerKind broker, string brokerSymbol)
    {
        lock (_gate)
            return _aliasToId.TryGetValue((broker, brokerSymbol), out var id) ? id : null;
    }

    public InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker)
    {
        lock (_gate)
        {
            if (_aliasToId.TryGetValue((broker, contract.Symbol), out var existing))
                return existing;

            var assetClass = AssetClassFromSecType(contract.SecType);
            var exchange = !string.IsNullOrWhiteSpace(contract.PrimaryExchange)
                ? contract.PrimaryExchange : contract.Exchange;
            var canonicalKey = (contract.Symbol, assetClass, exchange);

            if (!_canonicalToId.TryGetValue(canonicalKey, out var instrumentId))
            {
                instrumentId = _persistence.UpsertInstrument(new Instrument(
                    InstrumentId.None, contract.Symbol, assetClass, exchange,
                    string.IsNullOrWhiteSpace(contract.Currency) ? "USD" : contract.Currency,
                    TickSize: 0.01, Multiplier: 1.0));
                _byId[instrumentId.Value] = new Instrument(instrumentId, contract.Symbol, assetClass, exchange,
                    string.IsNullOrWhiteSpace(contract.Currency) ? "USD" : contract.Currency, 0.01, 1.0);
                _canonicalToId[canonicalKey] = instrumentId;
            }

            var alias = new InstrumentAlias(instrumentId, broker, contract.Symbol, null);
            _persistence.UpsertAlias(alias);
            _aliasToId[(broker, contract.Symbol)] = instrumentId;
            return instrumentId;
        }
    }

    public string? ToBrokerSymbol(InstrumentId id, BrokerKind broker)
    {
        lock (_gate)
        {
            foreach (var ((b, sym), mappedId) in _aliasToId)
                if (b == broker && mappedId == id) return sym;
            return null;
        }
    }

    public void RegisterAlias(InstrumentAlias alias)
    {
        lock (_gate)
        {
            _persistence.UpsertAlias(alias);
            _aliasToId[(alias.Broker, alias.BrokerSymbol)] = alias.InstrumentId;
        }
    }

    public IReadOnlyList<Instrument> All()
    {
        lock (_gate) return _byId.Values.ToList();
    }

    private void LoadCache()
    {
        foreach (var ins in _persistence.LoadInstruments())
        {
            _byId[ins.Id.Value] = ins;
            _canonicalToId[(ins.CanonicalSymbol, ins.AssetClass, ins.Exchange)] = ins.Id;
        }
        foreach (var (broker, symbol, id) in _persistence.LoadAliases())
            _aliasToId[(broker, symbol)] = id;

        _logger.LogInformation("Instrument registry loaded: {Instruments} instruments, {Aliases} aliases",
            _byId.Count, _aliasToId.Count);
    }

    /// <summary>Maps an IB-style security type string to a canonical <see cref="AssetClass"/>.</summary>
    private static AssetClass AssetClassFromSecType(string secType) => secType?.ToUpperInvariant() switch
    {
        "STK" or "EQUITY" or "ETF" => AssetClass.Equity,
        "FUT" or "FUTURE" or "CONTFUT" => AssetClass.Future,
        "CASH" or "FX" or "FOREX" => AssetClass.Forex,
        "CRYPTO" or "CRYPTOCURRENCY" => AssetClass.Crypto,
        "OPT" or "OPTION" or "FOP" => AssetClass.Option,
        "IND" or "INDEX" => AssetClass.Index,
        _ => AssetClass.Unknown,
    };

    public void Dispose() => _persistence.Dispose();
}
