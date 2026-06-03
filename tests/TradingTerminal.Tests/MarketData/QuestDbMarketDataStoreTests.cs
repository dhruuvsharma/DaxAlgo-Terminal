using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// Integration tests against the docker-compose QuestDB. They self-skip (return) when QuestDB isn't
/// reachable on the PG-wire port, so the suite stays green without Docker; bring it up
/// (<c>docker compose up -d</c>) to exercise the real QuestDB path. A random instrument id per run
/// avoids collisions on the shared dev database. QuestDB applies the WAL asynchronously after an ILP
/// flush, so reads poll briefly before asserting.
/// </summary>
public sealed class QuestDbMarketDataStoreTests
{
    private const string IlpConfig = "http::addr=localhost:9000;auto_flush=off;";
    private const string PgConnectionString =
        "Host=localhost;Port=8812;Database=qdb;Username=admin;Password=quest;Timeout=3;Command Timeout=10;ServerCompatibilityMode=NoTypeLoading";

    private static bool Reachable()
    {
        try { using var cn = new NpgsqlConnection(PgConnectionString); cn.Open(); return true; }
        catch { return false; }
    }

    private static QuestDbMarketDataStore NewStore() =>
        new(IlpConfig, PgConnectionString, persist: true, available: true, batchSize: 16,
            depthRetentionDays: 0, NullLogger<QuestDbMarketDataStore>.Instance);

    /// <summary>Poll a read until it returns the expected count or the timeout elapses (WAL apply lag).</summary>
    private static async Task<List<T>> ReadUntilAsync<T>(
        Func<IAsyncEnumerable<T>> read, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var got = new List<T>();
        while (DateTime.UtcNow < deadline)
        {
            got = new List<T>();
            await foreach (var x in read()) got.Add(x);
            if (got.Count >= expected) return got;
            await Task.Delay(150);
        }
        return got;
    }

    [Fact]
    public async Task Quotes_round_trip_against_questdb()
    {
        if (!Reachable()) return; // QuestDB not running — skip.

        using var store = NewStore();
        var id = new InstrumentId(Random.Shared.Next(1_000_000, 2_000_000));
        var t0 = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc);

        store.EnqueueQuote(new Quote(id, t0.AddSeconds(2), t0, 100, 101, 5, 6, BrokerKind.Alpaca, 2, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(1), t0, 99, 100, 1, 2, BrokerKind.Alpaca, 1, false));
        await store.FlushAsync();

        var got = await ReadUntilAsync(
            () => store.ReadQuotesAsync(id, t0, t0.AddMinutes(1)), 2, TimeSpan.FromSeconds(5));

        got.Should().HaveCount(2);
        got[0].Sequence.Should().Be(1);     // ordered by event_time ascending
        got[1].AskSize.Should().Be(6);
        got[0].Source.Should().Be(BrokerKind.Alpaca);
    }

    [Fact]
    public async Task Trades_round_trip_against_questdb()
    {
        if (!Reachable()) return; // QuestDB not running — skip.

        using var store = NewStore();
        var id = new InstrumentId(Random.Shared.Next(2_000_000, 3_000_000));
        var t0 = new DateTime(2026, 1, 2, 15, 0, 0, DateTimeKind.Utc);

        store.EnqueueTrade(new TradePrint(id, t0.AddSeconds(1), t0, 250.5, 10, AggressorSide.Buy, BrokerKind.InteractiveBrokers, 1, false));
        await store.FlushAsync();

        var got = await ReadUntilAsync(
            () => store.ReadTradesAsync(id, t0, t0.AddMinutes(1)), 1, TimeSpan.FromSeconds(5));

        got.Should().ContainSingle();
        got[0].Price.Should().Be(250.5);
        got[0].Aggressor.Should().Be(AggressorSide.Buy);
    }

    [Fact]
    public async Task Depth_snapshot_round_trips_levels_against_questdb()
    {
        if (!Reachable()) return; // QuestDB not running — skip.

        using var store = NewStore();
        var id = new InstrumentId(Random.Shared.Next(3_000_000, 4_000_000));
        var t0 = new DateTime(2026, 1, 2, 15, 30, 0, DateTimeKind.Utc);

        var snapshot = new DepthSnapshot(
            t0,
            Bids: new[] { new DepthLevel(100.0, 5), new DepthLevel(99.5, 8) },   // best bid first
            Asks: new[] { new DepthLevel(100.5, 3), new DepthLevel(101.0, 12) }); // best ask first
        store.EnqueueDepth(id, snapshot, BrokerKind.CTrader);
        await store.FlushAsync();

        var got = await ReadUntilAsync(
            () => store.ReadDepthAsync(id, t0, t0.AddMinutes(1)), 1, TimeSpan.FromSeconds(5));

        got.Should().ContainSingle();
        var s = got[0];
        s.Bids.Should().HaveCount(2);
        s.Asks.Should().HaveCount(2);
        s.BestBid.Should().Be(100.0);
        s.BestBidSize.Should().Be(5);
        s.BestAsk.Should().Be(100.5);
        s.Asks[1].Price.Should().Be(101.0);   // level order preserved
    }
}
