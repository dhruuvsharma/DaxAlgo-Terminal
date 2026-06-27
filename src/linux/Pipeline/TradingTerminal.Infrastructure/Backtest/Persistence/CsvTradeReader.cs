using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Streams trade prints from a CSV file — header <c>timestamp_micros,price,size,aggressor</c>
/// (epoch microseconds UTC; aggressor 1 = Buy, 2 = Sell, 0 = Unknown). The portable interop format
/// for an externally sourced trade tape (e.g. the Python harness feeding a real Binance tape).
/// Mirrors <see cref="ParquetTradeReader"/>.
/// </summary>
public static class CsvTradeReader
{
    public static async IAsyncEnumerable<TradePrint> ReadAsync(
        string path, DateTime? fromUtc = null, DateTime? toUtc = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fromMicros = fromUtc is { } f ? ToMicros(f) : long.MinValue;
        var toMicros   = toUtc   is { } t ? ToMicros(t) : long.MaxValue;
        var ic = CultureInfo.InvariantCulture;

        using var reader = new StreamReader(path);
        await reader.ReadLineAsync(ct).ConfigureAwait(false);   // skip header

        long seq = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length < 4) continue;
            var micros = long.Parse(c[0], ic);
            if (micros < fromMicros) continue;
            if (micros >= toMicros) yield break;
            var aggressor = int.Parse(c[3], ic) switch
            {
                1 => AggressorSide.Buy,
                2 => AggressorSide.Sell,
                _ => AggressorSide.Unknown,
            };
            var ts = FromMicros(micros);
            yield return new TradePrint(
                InstrumentId.None, ts, ts, double.Parse(c[1], ic), long.Parse(c[2], ic),
                aggressor, BrokerKind.Simulated, seq++, false);
        }
    }

    private static long ToMicros(DateTime d) => (EnsureUtc(d) - DateTime.UnixEpoch).Ticks / 10L;
    private static DateTime FromMicros(long m) => DateTime.UnixEpoch.AddTicks(m * 10L);
    private static DateTime EnsureUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
