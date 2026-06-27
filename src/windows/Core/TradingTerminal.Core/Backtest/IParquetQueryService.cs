using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Read-only analytical query layer over the Parquet tick archive produced by the live
/// recorder and the backtest writers. Backed by an embedded, in-process DuckDB engine so a
/// glob of Parquet files can be filtered, resampled, and aggregated with SQL predicate
/// pushdown — far cheaper than deserializing every row into C# the way
/// <c>ParquetTickReader</c> does for a single file.
///
/// Query-only by contract: this seam never writes to or mutates the Parquet files. The
/// recorder/backtest writers remain the only producers (rule: Parquet export is append-only).
/// Globs use forward slashes; on-disk Windows paths are normalized by the implementation.
/// </summary>
public interface IParquetQueryService
{
    /// <summary>
    /// Streams L1 ticks from one or more Parquet files (a single path or a glob such as
    /// <c>.../recordings/*.parquet</c>) with timestamp predicate pushdown. Range is half-open
    /// <c>[fromUtc, toUtc)</c>; null bounds mean unbounded. Rows are returned ordered by time.
    /// </summary>
    IAsyncEnumerable<Tick> ReadTicksAsync(
        string parquetGlob,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resamples L1 ticks to OHLCV-style bars of <paramref name="interval"/> entirely inside
    /// DuckDB, using the mid price <c>(bid+ask)/2</c> for open/high/low/close. Because the L1
    /// tape carries quote sizes rather than trade volume, each bar reports a
    /// <see cref="OhlcvAggregate.TickCount"/> as its activity proxy rather than a traded volume.
    /// Bars are returned ordered by bucket time; empty buckets are omitted.
    /// </summary>
    Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
        string parquetGlob,
        TimeSpan interval,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ad-hoc research escape hatch: runs arbitrary read-only SQL and returns a column-oriented
    /// result. The caller references Parquet inputs directly, e.g.
    /// <c>SELECT count(*) FROM read_parquet('C:/data/*.parquet')</c>. Intended for the
    /// Research/AI tabs; not on any hot path. The implementation opens an in-memory database, so
    /// statements that attempt to write to disk have nowhere persistent to land.
    /// </summary>
    Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default);
}

/// <summary>One resampled bar from <see cref="IParquetQueryService.AggregateBarsAsync"/>.
/// Prices are mid-price; <see cref="TickCount"/> is the number of L1 updates in the bucket.</summary>
public sealed record OhlcvAggregate(
    DateTime OpenTimeUtc,
    double Open,
    double High,
    double Low,
    double Close,
    long TickCount);

/// <summary>Column-oriented result of <see cref="IParquetQueryService.QueryAsync"/>. Each entry
/// in <see cref="Rows"/> is one row, positionally aligned to <see cref="Columns"/>; cells are
/// boxed CLR values (or null) as DuckDB materialized them.</summary>
public sealed record ParquetQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
