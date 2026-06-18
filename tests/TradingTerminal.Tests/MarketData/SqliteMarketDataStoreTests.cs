using System.IO;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class SqliteMarketDataStoreTests : IDisposable
{
    private readonly string _path;
    private readonly string _cs;

    public SqliteMarketDataStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mdstore_{Guid.NewGuid():N}.db");
        _cs = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
    }

    private SqliteMarketDataStore NewStore() =>
        new(_cs, persist: true, batchSize: 16, NullLogger<SqliteMarketDataStore>.Instance);

    [Fact]
    public async Task Quotes_round_trip_in_event_time_order()
    {
        using var store = NewStore();
        var id = new InstrumentId(1);
        var t0 = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc);

        store.EnqueueQuote(new Quote(id, t0.AddSeconds(2), t0, 100, 101, 5, 6, BrokerKind.Alpaca, 2, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(1), t0, 99, 100, 1, 2, BrokerKind.Alpaca, 1, false));
        await store.FlushAsync();

        var got = await Collect(store.ReadQuotesAsync(id, t0, t0.AddMinutes(1)));
        got.Should().HaveCount(2);
        got[0].Sequence.Should().Be(1);   // ordered by event_time ascending
        got[1].Sequence.Should().Be(2);
        got[1].Bid.Should().Be(100);
        got[1].AskSize.Should().Be(6);
    }

    [Fact]
    public async Task Trades_persist_aggressor_and_approx_flag()
    {
        using var store = NewStore();
        var id = new InstrumentId(3);
        var t = new DateTime(2026, 1, 2, 15, 0, 0, DateTimeKind.Utc);

        store.EnqueueTrade(new TradePrint(id, t, t, 250.5, 100, AggressorSide.Buy, BrokerKind.Alpaca, 1, true));
        await store.FlushAsync();

        var got = await Collect(store.ReadTradesAsync(id, t.AddMinutes(-1), t.AddMinutes(1)));
        got.Should().ContainSingle();
        got[0].Price.Should().Be(250.5);
        got[0].Aggressor.Should().Be(AggressorSide.Buy);
        got[0].EventTimeApproximate.Should().BeTrue();
    }

    [Fact]
    public async Task Bar_upsert_keeps_one_row_per_key_and_updates_close()
    {
        using var store = NewStore();
        var id = new InstrumentId(5);
        var open = new DateTime(2026, 1, 2, 16, 0, 0, DateTimeKind.Utc);

        store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open, 10, 11, 9, 10.5, 100, BrokerKind.CTrader, false));
        store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open, 10, 12, 9, 11.8, 250, BrokerKind.CTrader, true));
        await store.FlushAsync();

        var bars = await store.GetRecentBarsAsync(id, BarSize.OneMinute, 10);
        bars.Should().ContainSingle();
        bars[0].Close.Should().Be(11.8);
        bars[0].Volume.Should().Be(250);
        bars[0].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task GetRecentBars_returns_oldest_to_newest()
    {
        using var store = NewStore();
        var id = new InstrumentId(7);
        var open = new DateTime(2026, 1, 2, 16, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open.AddMinutes(i), i, i, i, i, 1, BrokerKind.Alpaca, true));
        await store.FlushAsync();

        var bars = await store.GetRecentBarsAsync(id, BarSize.OneMinute, 3);
        bars.Should().HaveCount(3);
        bars[0].OpenTimeUtc.Should().Be(open.AddMinutes(2)); // last 3, ascending
        bars[2].OpenTimeUtc.Should().Be(open.AddMinutes(4));
    }

    [Fact]
    public async Task Quote_reads_filter_by_source_when_set_and_merge_when_null()
    {
        using var store = NewStore();
        var id = new InstrumentId(11);
        var t0 = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc);

        // Two brokers stream the same instrument into the single file, interleaved in time.
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(1), t0, 100, 101, 1, 1, BrokerKind.InteractiveBrokers, 1, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(2), t0, 200, 201, 1, 1, BrokerKind.Binance, 1, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(3), t0, 102, 103, 1, 1, BrokerKind.InteractiveBrokers, 2, false));
        await store.FlushAsync();

        var ib = await Collect(store.ReadQuotesAsync(id, t0, t0.AddMinutes(1), BrokerKind.InteractiveBrokers));
        ib.Should().HaveCount(2);
        ib.Should().OnlyContain(q => q.Source == BrokerKind.InteractiveBrokers);

        var all = await Collect(store.ReadQuotesAsync(id, t0, t0.AddMinutes(1)));   // null source = merged
        all.Should().HaveCount(3);
        all.Select(q => q.EventTimeUtc).Should().BeInAscendingOrder();              // still ordered across sources
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> src)
    {
        var list = new List<T>();
        await foreach (var x in src) list.Add(x);
        return list;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }
}
