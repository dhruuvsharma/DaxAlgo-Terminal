using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Per-instrument facade over <see cref="IMarketDataStore"/>. Lets callers write code that reads
/// as if each instrument owned its own little store — useful for inspection, logging, and admin
/// operations that are conceptually scoped to one symbol — while the underlying store stays a
/// single time-partitioned table set.
///
/// <para>This is purely a syntactic layer: every method forwards to the same <c>IMarketDataStore</c>
/// the rest of the system uses. No data is copied, no caching is introduced, and there are no
/// behavioural differences vs calling the store methods directly. The point is readability:</para>
///
/// <code>
/// // Direct API:
/// await _store.GetRecentBarsAsync(aaplId, BarSize.OneMinute, 120, ct);
/// _store.ReadQuotesAsync(aaplId, fromUtc, toUtc, ct);
///
/// // Facade API:
/// var aapl = _store.Instrument(aaplId);
/// await aapl.RecentBars(BarSize.OneMinute, 120, ct);
/// aapl.Quotes(fromUtc, toUtc, ct);
/// </code>
///
/// Get one of these via the <see cref="MarketDataStoreInstrumentExtensions.Instrument"/>
/// extension method on any <see cref="IMarketDataStore"/>.
/// </summary>
public sealed class InstrumentDataView
{
    private readonly IMarketDataStore _store;

    /// <summary>The instrument this facade is scoped to.</summary>
    public InstrumentId InstrumentId { get; }

    internal InstrumentDataView(IMarketDataStore store, InstrumentId instrumentId)
    {
        _store = store;
        InstrumentId = instrumentId;
    }

    /// <summary>Stream persisted quotes in [from, to) ascending by event time. <paramref name="source"/>
    /// set = only that broker's quotes; <c>null</c> = all brokers merged.</summary>
    public IAsyncEnumerable<Quote> Quotes(DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, CancellationToken ct = default) =>
        _store.ReadQuotesAsync(InstrumentId, fromUtc, toUtc, source, ct);

    /// <summary>Stream persisted trade prints in [from, to) ascending by event time. <paramref name="source"/>
    /// set = only that broker's trades; <c>null</c> = all brokers merged.</summary>
    public IAsyncEnumerable<TradePrint> Trades(DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, CancellationToken ct = default) =>
        _store.ReadTradesAsync(InstrumentId, fromUtc, toUtc, source, ct);

    /// <summary>Stream persisted bars at the given cadence in [from, to) ascending by open time.
    /// <paramref name="source"/> set = only that broker's bars; <c>null</c> = all brokers merged.</summary>
    public IAsyncEnumerable<OhlcvBar> Bars(BarSize size, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, CancellationToken ct = default) =>
        _store.ReadBarsAsync(InstrumentId, size, fromUtc, toUtc, source, ct);

    /// <summary>Most recent <paramref name="count"/> bars at the given cadence, oldest→newest.
    /// <paramref name="source"/> set = only that broker's bars; <c>null</c> = all brokers merged.</summary>
    public Task<IReadOnlyList<OhlcvBar>> RecentBars(BarSize size, int count, BrokerKind? source = null, CancellationToken ct = default) =>
        _store.GetRecentBarsAsync(InstrumentId, size, count, source, ct);
}

/// <summary>Entry point for the per-instrument facade. Cheap to call — the returned view holds
/// just the <see cref="IMarketDataStore"/> reference and the <see cref="InstrumentId"/>.</summary>
public static class MarketDataStoreInstrumentExtensions
{
    /// <summary>Returns a per-instrument facade over this store.</summary>
    public static InstrumentDataView Instrument(this IMarketDataStore store, InstrumentId instrumentId) =>
        new(store, instrumentId);
}
