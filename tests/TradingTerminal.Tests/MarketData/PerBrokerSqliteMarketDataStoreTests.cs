using System.IO;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// Behaviour of the per-broker, per-stream SQLite store: writes route to one file per (broker,
/// stream), reads with a concrete source are isolated, null-source reads merge ascending across
/// files, same-instrument bars from two brokers don't collide, depth round-trips through the
/// dedicated -l2 file, and deleting one file wipes only that broker's stream.
/// </summary>
public sealed class PerBrokerSqliteMarketDataStoreTests : IDisposable
{
    private readonly string _dir;
    private const string Stem = "marketdata";

    public PerBrokerSqliteMarketDataStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"perbroker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    private PerBrokerSqliteMarketDataStore NewStore() =>
        new(_dir, Stem, persist: true, batchSize: 16, depthRetentionDays: 0, NullLoggerFactory.Instance);

    private string FileFor(BrokerKind b, string suffix) => Path.Combine(_dir, $"{Stem}-{b}-{suffix}.db");

    [Fact]
    public async Task Writes_create_one_file_per_broker_and_stream()
    {
        var id = new InstrumentId(1);
        var t = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc);
        using (var store = NewStore())
        {
            store.EnqueueQuote(new Quote(id, t, t, 100, 101, 1, 1, BrokerKind.InteractiveBrokers, 1, false));
            store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, t, 1, 2, 0, 1, 10, BrokerKind.InteractiveBrokers, true));
            store.EnqueueQuote(new Quote(id, t, t, 200, 201, 1, 1, BrokerKind.Binance, 1, false));
            await store.FlushAsync();
        }

        File.Exists(FileFor(BrokerKind.InteractiveBrokers, "l1")).Should().BeTrue();
        File.Exists(FileFor(BrokerKind.InteractiveBrokers, "bars")).Should().BeTrue();
        File.Exists(FileFor(BrokerKind.Binance, "l1")).Should().BeTrue();
        File.Exists(FileFor(BrokerKind.Binance, "bars")).Should().BeFalse();  // Binance wrote no bars
        File.Exists(Path.Combine(_dir, $"{Stem}.db")).Should().BeFalse();     // registry lives elsewhere
    }

    [Fact]
    public async Task Same_instrument_bars_from_two_brokers_do_not_collide()
    {
        var id = new InstrumentId(2);
        var open = new DateTime(2026, 1, 2, 16, 0, 0, DateTimeKind.Utc);
        using var store = NewStore();

        store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open, 10, 11, 9, 10.5, 100, BrokerKind.InteractiveBrokers, true));
        store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open, 20, 22, 18, 21.0, 200, BrokerKind.Binance, true));
        await store.FlushAsync();

        (await store.GetRecentBarsAsync(id, BarSize.OneMinute, 10, BrokerKind.InteractiveBrokers))
            .Should().ContainSingle().Which.Close.Should().Be(10.5);
        (await store.GetRecentBarsAsync(id, BarSize.OneMinute, 10, BrokerKind.Binance))
            .Should().ContainSingle().Which.Close.Should().Be(21.0);
    }

    [Fact]
    public async Task Null_source_quote_read_merges_ascending_across_brokers()
    {
        var id = new InstrumentId(3);
        var t0 = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc);
        using var store = NewStore();

        store.EnqueueQuote(new Quote(id, t0.AddSeconds(3), t0, 102, 103, 1, 1, BrokerKind.InteractiveBrokers, 2, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(1), t0, 100, 101, 1, 1, BrokerKind.InteractiveBrokers, 1, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(2), t0, 200, 201, 1, 1, BrokerKind.Binance, 1, false));
        await store.FlushAsync();

        var all = await Collect(store.ReadQuotesAsync(id, t0, t0.AddMinutes(1)));
        all.Should().HaveCount(3);
        all.Select(q => q.EventTimeUtc).Should().BeInAscendingOrder();
        all[1].Source.Should().Be(BrokerKind.Binance);

        var onlyIb = await Collect(store.ReadQuotesAsync(id, t0, t0.AddMinutes(1), BrokerKind.InteractiveBrokers));
        onlyIb.Should().HaveCount(2).And.OnlyContain(q => q.Source == BrokerKind.InteractiveBrokers);
    }

    [Fact]
    public async Task Depth_round_trips_through_the_l2_file()
    {
        var id = new InstrumentId(4);
        var t = new DateTime(2026, 1, 2, 15, 0, 0, DateTimeKind.Utc);
        using var store = NewStore();

        var snap = new DepthSnapshot(t,
            Bids: new[] { new DepthLevel(100.0, 5), new DepthLevel(99.5, 8) },
            Asks: new[] { new DepthLevel(100.5, 4), new DepthLevel(101.0, 9) });
        store.EnqueueDepth(id, snap, BrokerKind.InteractiveBrokers);
        await store.FlushAsync();

        File.Exists(FileFor(BrokerKind.InteractiveBrokers, "l2")).Should().BeTrue();

        var got = await Collect(store.ReadDepthAsync(id, t.AddMinutes(-1), t.AddMinutes(1)));
        got.Should().ContainSingle();
        got[0].TimestampUtc.Should().Be(t);
        got[0].Bids.Should().HaveCount(2);
        got[0].Bids[0].Price.Should().Be(100.0);     // best bid first, order preserved
        got[0].Asks[0].Price.Should().Be(100.5);
        got[0].Asks[1].Size.Should().Be(9);
    }

    [Fact]
    public async Task Source_scoped_read_of_unknown_broker_is_empty()
    {
        var id = new InstrumentId(5);
        var t = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc);
        using var store = NewStore();
        store.EnqueueQuote(new Quote(id, t, t, 100, 101, 1, 1, BrokerKind.InteractiveBrokers, 1, false));
        await store.FlushAsync();

        (await Collect(store.ReadQuotesAsync(id, t.AddMinutes(-1), t.AddMinutes(1), BrokerKind.Alpaca)))
            .Should().BeEmpty();
        File.Exists(FileFor(BrokerKind.Alpaca, "l1")).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_one_broker_stream_file_wipes_only_that_broker()
    {
        var id = new InstrumentId(6);
        var t = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc);
        using (var store = NewStore())
        {
            store.EnqueueQuote(new Quote(id, t, t, 100, 101, 1, 1, BrokerKind.InteractiveBrokers, 1, false));
            store.EnqueueQuote(new Quote(id, t, t, 200, 201, 1, 1, BrokerKind.Binance, 1, false));
            await store.FlushAsync();
        }

        SqliteConnection.ClearAllPools();
        File.Delete(FileFor(BrokerKind.Binance, "l1"));

        using var reopened = NewStore();
        (await Collect(reopened.ReadQuotesAsync(id, t.AddMinutes(-1), t.AddMinutes(1), BrokerKind.InteractiveBrokers)))
            .Should().ContainSingle();
        (await Collect(reopened.ReadQuotesAsync(id, t.AddMinutes(-1), t.AddMinutes(1), BrokerKind.Binance)))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task GetDataExtent_spans_all_broker_files()
    {
        var id = new InstrumentId(7);
        var t0 = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc);
        using var store = NewStore();
        store.EnqueueQuote(new Quote(id, t0, t0, 100, 101, 1, 1, BrokerKind.InteractiveBrokers, 1, false));
        store.EnqueueQuote(new Quote(id, t0.AddHours(2), t0, 200, 201, 1, 1, BrokerKind.Binance, 1, false));
        await store.FlushAsync();

        var extent = await store.GetDataExtentAsync();
        extent.EarliestUtc.Should().Be(t0);
        extent.LatestUtc.Should().Be(t0.AddHours(2));
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
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
