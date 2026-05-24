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

    /// <summary>Flush any queued records to disk now. Mainly for tests and graceful shutdown.</summary>
    Task FlushAsync(CancellationToken ct = default);

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
}
