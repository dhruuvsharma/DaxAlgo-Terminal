using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Non-Windows fallback for <see cref="IParquetQueryService"/>. The DuckDB-backed implementation is
/// Windows-only in this build (the bundled DuckDB native engine crashes the runtime on Linux/ARM),
/// so this stub fails loudly if the optional Parquet analytical-query path is invoked off-Windows.
/// The canonical stores (SQLite/Postgres/QuestDB) and <c>ParquetTickReader</c> are unaffected — only
/// the DuckDB-over-Parquet SQL layer (used by the Research/AI tabs, all Windows-only UI) is gated.
/// </summary>
public sealed class UnsupportedParquetQueryService : IParquetQueryService
{
    private const string Message =
        "The DuckDB Parquet query layer is only available on the Windows build of DaxAlgo Terminal.";

    public IAsyncEnumerable<Tick> ReadTicksAsync(
        string parquetGlob, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(Message);

    public Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
        string parquetGlob, TimeSpan interval, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(Message);

    public Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(Message);
}
