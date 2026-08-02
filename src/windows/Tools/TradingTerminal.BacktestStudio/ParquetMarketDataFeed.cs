using System.Runtime.CompilerServices;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Persistence;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// A feed that replays a recorded parquet tick file through the new engine, reusing Infrastructure's
/// <see cref="ParquetTickReader"/>. Single-instrument (parquet ticks carry no canonical id) — every
/// quote is tagged with the supplied instrument id. Lives in the Studio because it bridges the
/// Infrastructure reader to the engine's feed seam.
/// </summary>
public sealed class ParquetMarketDataFeed : IMarketDataFeed
{
    private readonly InstrumentId _instrument;
    private readonly string _path;
    private readonly DateTime? _fromUtc;
    private readonly DateTime? _toUtc;

    public ParquetMarketDataFeed(InstrumentId instrument, string path, DateTime? fromUtc, DateTime? toUtc)
    {
        _instrument = instrument;
        _path = path;
        _fromUtc = fromUtc;
        _toUtc = toUtc;
    }

    public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tick in ParquetTickReader.ReadAsync(_path, _fromUtc, _toUtc, ct).WithCancellation(ct))
            yield return MarketEvent.OfQuote(_instrument, tick);
    }
}
