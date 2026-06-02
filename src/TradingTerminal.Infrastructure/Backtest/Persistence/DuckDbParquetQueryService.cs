using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// <see cref="IParquetQueryService"/> backed by an embedded DuckDB engine. Each call opens a
/// fresh in-memory database (cheap; isolated; thread-safe) and reads the on-disk Parquet files
/// directly via <c>read_parquet(...)</c>, so filtering and aggregation run in the engine with
/// predicate pushdown instead of materializing every row in C#.
///
/// The Parquet schema is the one written by <see cref="ParquetTickWriter"/>:
/// <c>TimestampMicros</c> (epoch microseconds UTC), <c>Bid</c>, <c>Ask</c>, <c>BidSize</c>,
/// <c>AskSize</c>. Timestamps stay integer micros on the wire and are reconstructed to
/// <see cref="DateTime"/> (UTC) on the way out — never strings, no timezone ambiguity.
/// </summary>
public sealed class DuckDbParquetQueryService : IParquetQueryService
{
    private const string InMemory = "Data Source=:memory:";
    private readonly ILogger<DuckDbParquetQueryService> _logger;

    public DuckDbParquetQueryService(ILogger<DuckDbParquetQueryService> logger) => _logger = logger;

    public async IAsyncEnumerable<Tick> ReadTicksAsync(
        string parquetGlob,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (from, to) = MicrosRange(fromUtc, toUtc);
        var sql = $"""
            SELECT TimestampMicros, Bid, Ask, BidSize, AskSize
            FROM read_parquet('{SqlPath(parquetGlob)}')
            WHERE TimestampMicros >= {from.ToString(CultureInfo.InvariantCulture)}
              AND TimestampMicros <  {to.ToString(CultureInfo.InvariantCulture)}
            ORDER BY TimestampMicros
            """;

        await using var cn = new DuckDBConnection(InMemory);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new Tick(
                FromEpochMicros(reader.GetInt64(0)),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetInt64(3),
                reader.GetInt64(4));
        }
    }

    public async Task<IReadOnlyList<OhlcvAggregate>> AggregateBarsAsync(
        string parquetGlob,
        TimeSpan interval,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Bar interval must be positive.");

        var bucketMicros = interval.Ticks / 10L; // 100ns ticks → microseconds
        var (from, to) = MicrosRange(fromUtc, toUtc);
        var sql = $"""
            WITH t AS (
                SELECT make_timestamp(TimestampMicros) AS ts, (Bid + Ask) / 2.0 AS mid
                FROM read_parquet('{SqlPath(parquetGlob)}')
                WHERE TimestampMicros >= {from.ToString(CultureInfo.InvariantCulture)}
                  AND TimestampMicros <  {to.ToString(CultureInfo.InvariantCulture)}
            )
            SELECT
                time_bucket(to_microseconds({bucketMicros.ToString(CultureInfo.InvariantCulture)}), ts) AS bucket,
                arg_min(mid, ts) AS o,
                max(mid)         AS h,
                min(mid)         AS l,
                arg_max(mid, ts) AS c,
                count(*)         AS n
            FROM t
            GROUP BY bucket
            ORDER BY bucket
            """;

        await using var cn = new DuckDBConnection(InMemory);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;

        var bars = new List<OhlcvAggregate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            bars.Add(new OhlcvAggregate(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetInt64(5)));
        }
        return bars;
    }

    public async Task<ParquetQueryResult> QueryAsync(string sql, CancellationToken ct = default)
    {
        await using var cn = new DuckDBConnection(InMemory);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++) columns[i] = reader.GetName(i);

        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new ParquetQueryResult(columns, rows);
    }

    /// <summary>Normalizes a filesystem path/glob for embedding in a SQL string literal:
    /// forward slashes (DuckDB convention, works on Windows) and doubled single quotes.</summary>
    private static string SqlPath(string path) => path.Replace('\\', '/').Replace("'", "''");

    private static (long From, long To) MicrosRange(DateTime? fromUtc, DateTime? toUtc) =>
        (fromUtc is { } f ? ToEpochMicros(EnsureUtc(f)) : long.MinValue,
         toUtc   is { } t ? ToEpochMicros(EnsureUtc(t)) : long.MaxValue);

    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt.ToUniversalTime(),
    };

    private static long ToEpochMicros(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks / 10L;

    private static DateTime FromEpochMicros(long micros) => DateTime.UnixEpoch.AddTicks(micros * 10L);
}
