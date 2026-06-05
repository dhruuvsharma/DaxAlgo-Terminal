using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// The local persistence seam for normalized market data. Writes are <em>non-blocking</em>:
/// the <c>Enqueue*</c> methods hand the record to a background batch writer and return
/// immediately, so the ingest hot path never waits on disk. Reads are async queries against the
/// persisted history — used for strategy warm-up, replay, and research, never per-tick.
/// </summary>
public interface IMarketDataStore
{
    /// <summary>Queue a quote for batched persistence. Returns immediately.</summary>
    void EnqueueQuote(Quote quote);

    /// <summary>Queue a trade print for batched persistence. Returns immediately.</summary>
    void EnqueueTrade(TradePrint trade);

    /// <summary>Queue a bar (insert, or upsert when an in-progress bar of the same key already
    /// exists). Returns immediately.</summary>
    void EnqueueBar(OhlcvBar bar);

    /// <summary>Queue an L2 depth snapshot for batched persistence. Returns immediately. Unlike the
    /// other streams, depth is only persisted by backends purpose-built for its volume (QuestDB);
    /// the SQLite and Postgres stores ignore depth, so this is a no-op there by design.</summary>
    void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source);

    /// <summary>Flush any queued records to disk now. Mainly for tests and graceful shutdown.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Earliest and latest event time held in the store across the persisted tables
    /// (quotes/trades/bars, plus depth where the backend stores it). Drives the archive coverage /
    /// instant-offload view. Default implementation reports "no data" so backends and fakes that
    /// don't track an extent keep working; the real stores override it.</summary>
    Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default) =>
        Task.FromResult(StoredDataExtent.Empty);

    /// <summary>Most recent <paramref name="count"/> bars for an instrument/size, oldest→newest,
    /// for strategy warm-up.</summary>
    Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default);

    /// <summary>Stream stored quotes in [from, to) ascending by event time (replay/research).</summary>
    IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Stream stored trades in [from, to) ascending by event time (replay/research).</summary>
    IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Stream reconstructed L2 depth snapshots in [from, to) ascending by event time. Backends
    /// that don't persist depth return an empty sequence.</summary>
    IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Stream stored bars at <paramref name="size"/> in [from, to) ascending by open
    /// time. Used by the archiver to export a bar table for a given period.</summary>
    IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default);

    /// <summary>Delete every quote in [from, to) across all instruments. Used by the archiver
    /// after a successful, verified upload. Returns the number of rows removed.</summary>
    Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Delete every trade in [from, to) across all instruments.</summary>
    Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Delete every bar in [from, to) across all instruments / bar sizes.</summary>
    Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Delete every depth row in [from, to) across all instruments. Returns the number of
    /// rows removed, or -1 when the backend reports an unknown count (e.g. QuestDB partition drop).
    /// Backends that don't persist depth return 0.</summary>
    Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
