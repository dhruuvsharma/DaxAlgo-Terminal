using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// A single replay event the engine consumes: either a quote update or a trade print, never
/// both. Modelled as a struct with nullable reference fields so the backtester avoids
/// per-event boxing/allocation at the scale of tens of millions of events per run.
/// Exactly one of <see cref="Quote"/> / <see cref="Trade"/> is non-null.
/// </summary>
internal readonly record struct BacktestEvent(DateTime TimestampUtc, Tick? Quote, TradePrint? Trade)
{
    public static BacktestEvent FromQuote(Tick q) => new(q.TimestampUtc, q, null);
    public static BacktestEvent FromTrade(TradePrint t) => new(t.EventTimeUtc, null, t);
}

/// <summary>
/// Internal seam over the two tick sources the engine can replay from:
/// a parquet file (legacy, quote-only) or the canonical local store (quotes + trades merged
/// by event time). <see cref="BacktestSession"/> picks the right concrete via
/// <see cref="Resolve"/> based on the config — callers don't construct sources directly.
/// </summary>
internal static class BacktestTickSource
{
    /// <summary>Yields the event stream the engine should replay for this config. For the
    /// parquet source every event is a quote (the legacy recorder doesn't capture trades).
    /// For the local-store source quotes and trades are interleaved by event time.</summary>
    public static IAsyncEnumerable<BacktestEvent> Resolve(BacktestConfig config, IMarketDataStore? store, CancellationToken ct)
    {
        return config.Source switch
        {
            BacktestDataSource.LocalStore => ReadFromStore(config, store
                ?? throw new InvalidOperationException(
                    "BacktestConfig.Source = LocalStore but no IMarketDataStore was supplied to the session."), ct),
            _ => ReadFromParquet(config, ct),
        };
    }

    private static IAsyncEnumerable<BacktestEvent> ReadFromParquet(
        BacktestConfig config, CancellationToken ct)
    {
        // Quote-only (legacy) unless an optional trade tape is supplied alongside; then merge both by
        // event time so trade-tape-primary strategies replay genuine prints (not synthetic L1).
        return string.IsNullOrWhiteSpace(config.TradeDataPath)
            ? ReadQuotesOnly(config, ct)
            : ReadQuotesAndTrades(config, ct);
    }

    private static async IAsyncEnumerable<BacktestEvent> ReadQuotesOnly(
        BacktestConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var t in ReadQuotes(config.TickDataPath, config.FromUtc, config.ToUtc, ct))
            yield return BacktestEvent.FromQuote(t);
    }

    // Route by extension: .csv → the portable CSV readers (external/Python-sourced data), else the
    // native parquet readers (recorder / synth / C#-written tape).
    private static bool IsCsv(string? path) =>
        path is not null && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static IAsyncEnumerable<Tick> ReadQuotes(string path, DateTime? from, DateTime? to, CancellationToken ct) =>
        IsCsv(path) ? CsvTickReader.ReadAsync(path, from, to, ct) : ParquetTickReader.ReadAsync(path, from, to, ct);

    private static IAsyncEnumerable<TradePrint> ReadTrades(string path, DateTime? from, DateTime? to, CancellationToken ct) =>
        IsCsv(path) ? CsvTradeReader.ReadAsync(path, from, to, ct) : ParquetTradeReader.ReadAsync(path, from, to, ct);

    /// <summary>
    /// Merges the quote parquet and an optional trade parquet by event time, mirroring the store
    /// merge (<see cref="ReadFromStore"/>). On a tie the quote is yielded first so the strategy's
    /// view of the spread is current when it sees the trade.
    /// </summary>
    private static async IAsyncEnumerable<BacktestEvent> ReadQuotesAndTrades(
        BacktestConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var qe = ReadQuotes(config.TickDataPath, config.FromUtc, config.ToUtc, ct).GetAsyncEnumerator(ct);
        await using var te = ReadTrades(config.TradeDataPath!, config.FromUtc, config.ToUtc, ct).GetAsyncEnumerator(ct);

        var hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
        var hasT = await te.MoveNextAsync().ConfigureAwait(false);
        while (hasQ || hasT)
        {
            if (hasQ && (!hasT || qe.Current.TimestampUtc <= te.Current.EventTimeUtc))
            {
                yield return BacktestEvent.FromQuote(qe.Current);
                hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                yield return BacktestEvent.FromTrade(te.Current);
                hasT = await te.MoveNextAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads both quotes and trades from the store and merges them by event time. Quotes are
    /// projected back to legacy <see cref="Tick"/>s so the engine's order-book / fill-context
    /// code stays unchanged. On a tie (same event time) the quote is yielded first so the
    /// strategy's view of the spread is current when it sees the trade.
    /// </summary>
    private static async IAsyncEnumerable<BacktestEvent> ReadFromStore(
        BacktestConfig config, IMarketDataStore store,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (config.InstrumentId.IsNone)
            throw new InvalidOperationException("LocalStore backtest requires BacktestConfig.InstrumentId.");
        if (config.FromUtc is not { } from || config.ToUtc is not { } to)
            throw new InvalidOperationException("LocalStore backtest requires both FromUtc and ToUtc.");
        if (to <= from)
            throw new InvalidOperationException("LocalStore backtest requires ToUtc > FromUtc.");

        // config.Broker scopes the read to one broker when the store is split per broker; null reads
        // every broker's data merged (the only sensible default for the single-file backend).
        await using var qe = store.ReadQuotesAsync(config.InstrumentId, from, to, config.Broker, ct).GetAsyncEnumerator(ct);
        await using var te = store.ReadTradesAsync(config.InstrumentId, from, to, config.Broker, ct).GetAsyncEnumerator(ct);

        var hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
        var hasT = await te.MoveNextAsync().ConfigureAwait(false);
        while (hasQ || hasT)
        {
            if (hasQ && (!hasT || qe.Current.EventTimeUtc <= te.Current.EventTimeUtc))
            {
                var q = qe.Current;
                yield return BacktestEvent.FromQuote(new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize));
                hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                yield return BacktestEvent.FromTrade(te.Current);
                hasT = await te.MoveNextAsync().ConfigureAwait(false);
            }
        }
    }
}
