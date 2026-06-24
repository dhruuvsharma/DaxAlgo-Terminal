using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

/// <summary>
/// Covers the optional backtest trade tape: trades round-trip through parquet with their aggressor
/// side and event time preserved. This is the path that lets the Python harness feed a real crypto
/// tape into a parquet-source backtest (so trade-tape-primary strategies replay genuine prints).
/// </summary>
public sealed class ParquetTradeTapeTests
{
    [Fact]
    public async Task Trades_RoundTrip_PreservingAggressorAndTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bt-trades-{Guid.NewGuid():N}.parquet");
        try
        {
            var t0 = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
            var written = new[]
            {
                new TradePrint(InstrumentId.None, t0, t0, 100.25, 3, AggressorSide.Buy, BrokerKind.Binance, 0, false),
                new TradePrint(InstrumentId.None, t0.AddMilliseconds(250), t0.AddMilliseconds(250), 100.20, 5, AggressorSide.Sell, BrokerKind.Binance, 1, false),
                new TradePrint(InstrumentId.None, t0.AddSeconds(1), t0.AddSeconds(1), 100.30, 1, AggressorSide.Unknown, BrokerKind.Binance, 2, false),
            };

            await using (var w = new ParquetTradeWriter(path))
                foreach (var t in written) await w.WriteAsync(t);

            var read = new List<TradePrint>();
            await foreach (var t in ParquetTradeReader.ReadAsync(path)) read.Add(t);

            read.Should().HaveCount(3);
            read[0].Aggressor.Should().Be(AggressorSide.Buy);
            read[0].Price.Should().Be(100.25);
            read[0].Size.Should().Be(3);
            read[1].Aggressor.Should().Be(AggressorSide.Sell);
            read[2].Aggressor.Should().Be(AggressorSide.Unknown);
            read[1].EventTimeUtc.Should().Be(t0.AddMilliseconds(250));
            // Reader assigns monotone sequence by row order.
            read.Select(t => t.Sequence).Should().BeInAscendingOrder();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task TimeRangeFilter_IsHalfOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bt-trades-{Guid.NewGuid():N}.parquet");
        try
        {
            var t0 = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
            await using (var w = new ParquetTradeWriter(path))
                for (var i = 0; i < 10; i++)
                    await w.WriteAsync(new TradePrint(InstrumentId.None, t0.AddSeconds(i), t0.AddSeconds(i),
                        100 + i, 1, AggressorSide.Buy, BrokerKind.Binance, i, false));

            var read = new List<TradePrint>();
            await foreach (var t in ParquetTradeReader.ReadAsync(path, t0.AddSeconds(2), t0.AddSeconds(5)))
                read.Add(t);

            // [2, 5) ⇒ seconds 2,3,4.
            read.Should().HaveCount(3);
            read.First().EventTimeUtc.Should().Be(t0.AddSeconds(2));
            read.Last().EventTimeUtc.Should().Be(t0.AddSeconds(4));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
