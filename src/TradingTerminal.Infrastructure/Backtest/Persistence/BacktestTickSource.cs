using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Internal seam over the two tick sources the engine can replay from:
/// a parquet file (legacy) or the canonical local store. <see cref="BacktestSession"/>
/// picks the right concrete via <see cref="Resolve"/> based on the config — callers
/// don't construct sources directly.
/// </summary>
internal static class BacktestTickSource
{
    /// <summary>Yields the tick stream the engine should replay for this config.</summary>
    public static IAsyncEnumerable<Tick> Resolve(BacktestConfig config, IMarketDataStore? store, CancellationToken ct)
    {
        return config.Source switch
        {
            BacktestDataSource.LocalStore => ReadFromStore(config, store
                ?? throw new InvalidOperationException(
                    "BacktestConfig.Source = LocalStore but no IMarketDataStore was supplied to the session."), ct),
            _ => ParquetTickReader.ReadAsync(config.TickDataPath, config.FromUtc, config.ToUtc, ct),
        };
    }

    /// <summary>
    /// Projects canonical <see cref="Quote"/>s from the store back to legacy <see cref="Tick"/>s
    /// so the rest of the engine stays untouched. The store returns quotes ordered by event time
    /// ascending — exactly what the engine expects.
    /// </summary>
    private static async IAsyncEnumerable<Tick> ReadFromStore(
        BacktestConfig config, IMarketDataStore store,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (config.InstrumentId.IsNone)
            throw new InvalidOperationException("LocalStore backtest requires BacktestConfig.InstrumentId.");
        if (config.FromUtc is not { } from || config.ToUtc is not { } to)
            throw new InvalidOperationException("LocalStore backtest requires both FromUtc and ToUtc.");
        if (to <= from)
            throw new InvalidOperationException("LocalStore backtest requires ToUtc > FromUtc.");

        await foreach (var q in store.ReadQuotesAsync(config.InstrumentId, from, to, ct))
            yield return new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize);
    }
}
