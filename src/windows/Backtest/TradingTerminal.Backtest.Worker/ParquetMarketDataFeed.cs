using System.Runtime.CompilerServices;
using Parquet.Serialization;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Persistence;

namespace TradingTerminal.Backtest.Worker;

/// <summary>P2's narrow, single-instrument immutable parquet adapter.</summary>
internal sealed class ParquetMarketDataFeed(FileStream verifiedInput) : IMarketDataFeed
{
    public async IAsyncEnumerable<MarketEvent> StreamAsync(
        RunSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!spec.Universe.IsSingleInstrument)
            throw new NotSupportedException("P2 parquet jobs support exactly one instrument.");

        var instrument = spec.Universe.Primary.Id;
        var fromMicros = spec.Data.FromUtc is { } from ? ToEpochMicros(EnsureUtc(from)) : long.MinValue;
        var toMicros = spec.Data.ToUtc is { } to ? ToEpochMicros(EnsureUtc(to)) : long.MaxValue;
        long? previousTimestamp = null;

        verifiedInput.Position = 0;
        var rows = ParquetSerializer.DeserializeAllAsync<TickRecord>(
            verifiedInput,
            cancellationToken: ct);
        await foreach (var record in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            if (previousTimestamp is { } previous && record.TimestampMicros < previous)
                throw new InvalidDataException("Parquet ticks are not ordered by ascending UTC timestamp.");
            previousTimestamp = record.TimestampMicros;

            if (record.TimestampMicros < fromMicros) continue;
            if (record.TimestampMicros >= toMicros) yield break;

            var tick = new Tick(
                FromEpochMicros(record.TimestampMicros),
                record.Bid,
                record.Ask,
                record.BidSize,
                record.AskSize);
            yield return MarketEvent.OfQuote(instrument, tick);
        }
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static long ToEpochMicros(DateTime value) =>
        (value - DateTime.UnixEpoch).Ticks / 10L;

    private static DateTime FromEpochMicros(long value) =>
        DateTime.UnixEpoch.AddTicks(checked(value * 10L));
}
