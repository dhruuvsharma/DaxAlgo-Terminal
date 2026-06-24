using System.IO;
using System.Runtime.CompilerServices;
using Parquet.Serialization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Reads trade prints from a parquet file (<see cref="TradeRecord"/> schema) as an async stream,
/// mirroring <see cref="ParquetTickReader"/>. Supports half-open <c>[fromUtc, toUtc)</c> filtering.
/// Used to feed a real trade tape into a parquet-source backtest so trade-tape-primary strategies
/// replay genuine prints (q = 1.0) rather than the synthetic-L1 fallback.
/// </summary>
public static class ParquetTradeReader
{
    public static async IAsyncEnumerable<TradePrint> ReadAsync(
        string path,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fromMicros = fromUtc is { } f ? ToEpochMicros(EnsureUtc(f)) : long.MinValue;
        var toMicros   = toUtc   is { } t ? ToEpochMicros(EnsureUtc(t)) : long.MaxValue;

        await using var stream = File.OpenRead(path);
        var rows = ParquetSerializer.DeserializeAllAsync<TradeRecord>(stream, cancellationToken: ct);

        long seq = 0;
        await foreach (var r in rows.WithCancellation(ct))
        {
            if (r.TimestampMicros < fromMicros) continue;
            if (r.TimestampMicros >= toMicros) yield break;

            var ts = FromEpochMicros(r.TimestampMicros);
            var aggressor = r.Aggressor switch
            {
                1 => AggressorSide.Buy,
                2 => AggressorSide.Sell,
                _ => AggressorSide.Unknown,
            };
            // InstrumentId is irrelevant to the engine's OnTradeAsync (it reads price/size/aggressor/
            // time); Source is cosmetic in backtest. Sequence is the file row order.
            yield return new TradePrint(
                InstrumentId.None, ts, ts, r.Price, r.Size, aggressor, BrokerKind.Simulated, seq++, false);
        }
    }

    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt.ToUniversalTime(),
    };

    private static long ToEpochMicros(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks / 10L;
    private static DateTime FromEpochMicros(long micros) => DateTime.UnixEpoch.AddTicks(micros * 10L);
}
