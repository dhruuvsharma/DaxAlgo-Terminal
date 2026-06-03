using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Routes the canonical store seam across two backends by data kind: the high-volume L1/L2 streams
/// (quotes, trades, depth) go to <paramref name="tickStore"/> — QuestDB — while bars stay in
/// <paramref name="barStore"/> — SQLite. Reads and range-deletes dispatch to whichever backend owns
/// that stream. This is the store registered when <see cref="Core.Configuration.MarketDataProvider.QuestDb"/>
/// is selected; everything above the <see cref="IMarketDataStore"/> seam is unaware of the split.
/// </summary>
internal sealed class CompositeMarketDataStore : IMarketDataStore, IDisposable
{
    private readonly IMarketDataStore _tickStore;
    private readonly IMarketDataStore _barStore;

    public CompositeMarketDataStore(IMarketDataStore tickStore, IMarketDataStore barStore, ILogger logger)
    {
        _tickStore = tickStore;
        _barStore = barStore;
        logger.LogInformation("Market-data store: QuestDB (L1/L2) + SQLite (bars).");
    }

    public void EnqueueQuote(Quote quote) => _tickStore.EnqueueQuote(quote);
    public void EnqueueTrade(TradePrint trade) => _tickStore.EnqueueTrade(trade);
    public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source) =>
        _tickStore.EnqueueDepth(instrumentId, snapshot, source);
    public void EnqueueBar(OhlcvBar bar) => _barStore.EnqueueBar(bar);

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _tickStore.FlushAsync(ct).ConfigureAwait(false);
        await _barStore.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default) =>
        _barStore.GetRecentBarsAsync(instrumentId, size, count, ct);

    public IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _barStore.ReadBarsAsync(instrumentId, size, fromUtc, toUtc, ct);

    public IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _tickStore.ReadQuotesAsync(instrumentId, fromUtc, toUtc, ct);

    public IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _tickStore.ReadTradesAsync(instrumentId, fromUtc, toUtc, ct);

    public IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _tickStore.ReadDepthAsync(instrumentId, fromUtc, toUtc, ct);

    public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _tickStore.DeleteQuotesInRangeAsync(fromUtc, toUtc, ct);

    public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _tickStore.DeleteTradesInRangeAsync(fromUtc, toUtc, ct);

    public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _barStore.DeleteBarsInRangeAsync(fromUtc, toUtc, ct);

    public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _tickStore.DeleteDepthInRangeAsync(fromUtc, toUtc, ct);

    public void Dispose()
    {
        (_tickStore as IDisposable)?.Dispose();
        (_barStore as IDisposable)?.Dispose();
    }
}
