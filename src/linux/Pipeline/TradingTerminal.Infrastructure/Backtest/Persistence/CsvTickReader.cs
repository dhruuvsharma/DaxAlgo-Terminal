using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Streams quotes from a CSV file — header <c>timestamp_micros,bid,ask,bid_size,ask_size</c>
/// (epoch microseconds UTC). This is the portable cross-language interop format for externally
/// sourced data (e.g. the Python backtest harness), chosen because parquet writer/reader versions
/// across ecosystems don't always agree on the footer Thrift schema. Half-open <c>[from, to)</c>
/// filtering mirrors <see cref="ParquetTickReader"/>.
/// </summary>
public static class CsvTickReader
{
    public static async IAsyncEnumerable<Tick> ReadAsync(
        string path, DateTime? fromUtc = null, DateTime? toUtc = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fromMicros = fromUtc is { } f ? ToMicros(f) : long.MinValue;
        var toMicros   = toUtc   is { } t ? ToMicros(t) : long.MaxValue;
        var ic = CultureInfo.InvariantCulture;

        using var reader = new StreamReader(path);
        await reader.ReadLineAsync(ct).ConfigureAwait(false);   // skip header

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length < 5) continue;
            var micros = long.Parse(c[0], ic);
            if (micros < fromMicros) continue;
            if (micros >= toMicros) yield break;
            yield return new Tick(
                FromMicros(micros),
                double.Parse(c[1], ic), double.Parse(c[2], ic),
                long.Parse(c[3], ic), long.Parse(c[4], ic));
        }
    }

    private static long ToMicros(DateTime d) => (EnsureUtc(d) - DateTime.UnixEpoch).Ticks / 10L;
    private static DateTime FromMicros(long m) => DateTime.UnixEpoch.AddTicks(m * 10L);
    private static DateTime EnsureUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
