using System.IO;
using System.Runtime.CompilerServices;
using Parquet.Serialization;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Reads ticks from a parquet file as an async stream. Supports half-open
/// <c>[fromUtc, toUtc)</c> filtering so backtests can scope to a date range without
/// rewriting files. Row groups are read sequentially; within a group we filter and
/// yield, so memory use is bounded by row group size, not file size.
/// </summary>
public static class ParquetTickReader
{
    public static async IAsyncEnumerable<Tick> ReadAsync(
        string path,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fromMicros = fromUtc is { } f ? ToEpochMicros(EnsureUtc(f)) : long.MinValue;
        var toMicros   = toUtc   is { } t ? ToEpochMicros(EnsureUtc(t)) : long.MaxValue;

        await using var stream = File.OpenRead(path);
        var rows = ParquetSerializer.DeserializeAllAsync<TickRecord>(stream, cancellationToken: ct);

        await foreach (var record in rows.WithCancellation(ct))
        {
            if (record.TimestampMicros < fromMicros) continue;
            if (record.TimestampMicros >= toMicros) yield break;

            yield return new Tick(
                FromEpochMicros(record.TimestampMicros),
                record.Bid,
                record.Ask,
                record.BidSize,
                record.AskSize);
        }
    }

    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt.ToUniversalTime(),
    };

    private static long ToEpochMicros(DateTime utc) =>
        (utc - DateTime.UnixEpoch).Ticks / 10L;

    private static DateTime FromEpochMicros(long micros) =>
        DateTime.UnixEpoch.AddTicks(micros * 10L);
}
