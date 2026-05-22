using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// Integration tests against the docker-compose TimescaleDB. They self-skip (return) when the
/// database isn't reachable, so the suite stays green without Docker; bring the container up
/// (<c>docker compose up -d</c>) to exercise the real Postgres path. Uses a random instrument id
/// per run so concurrent/repeat runs don't collide on the shared dev database.
/// </summary>
public sealed class NpgsqlMarketDataStoreTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=daxalgo;Username=daxalgo;Password=daxalgo;Timeout=3;Command Timeout=10";

    private static bool Reachable()
    {
        try { using var cn = new NpgsqlConnection(ConnectionString); cn.Open(); return true; }
        catch { return false; }
    }

    [Fact]
    public async Task Quotes_round_trip_against_timescaledb()
    {
        if (!Reachable()) return; // Docker not running — skip.

        using var store = new NpgsqlMarketDataStore(ConnectionString, true, 16, NullLogger<NpgsqlMarketDataStore>.Instance);
        var id = new InstrumentId(Random.Shared.Next(1_000_000, 2_000_000));
        var t0 = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc);

        store.EnqueueQuote(new Quote(id, t0.AddSeconds(2), t0, 100, 101, 5, 6, BrokerKind.Alpaca, 2, false));
        store.EnqueueQuote(new Quote(id, t0.AddSeconds(1), t0, 99, 100, 1, 2, BrokerKind.Alpaca, 1, false));
        await store.FlushAsync();

        var got = new List<Quote>();
        await foreach (var q in store.ReadQuotesAsync(id, t0, t0.AddMinutes(1))) got.Add(q);

        got.Should().HaveCount(2);
        got[0].Sequence.Should().Be(1);   // ordered by event_time ascending
        got[1].AskSize.Should().Be(6);
    }

    [Fact]
    public async Task Bar_upsert_keeps_one_row_against_timescaledb()
    {
        if (!Reachable()) return; // Docker not running — skip.

        using var store = new NpgsqlMarketDataStore(ConnectionString, true, 16, NullLogger<NpgsqlMarketDataStore>.Instance);
        var id = new InstrumentId(Random.Shared.Next(2_000_000, 3_000_000));
        var open = new DateTime(2026, 1, 2, 16, 0, 0, DateTimeKind.Utc);

        store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open, 10, 11, 9, 10.5, 100, BrokerKind.CTrader, false));
        store.EnqueueBar(new OhlcvBar(id, BarSize.OneMinute, open, 10, 12, 9, 11.8, 250, BrokerKind.CTrader, true));
        await store.FlushAsync();

        var bars = await store.GetRecentBarsAsync(id, BarSize.OneMinute, 10);
        bars.Should().ContainSingle();
        bars[0].Close.Should().Be(11.8);
        bars[0].IsFinal.Should().BeTrue();
    }
}
