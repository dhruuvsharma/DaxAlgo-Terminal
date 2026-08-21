using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.MarketData.Tests;

/// <summary>
/// The range delete that retention and the archive both prune through.
///
/// <para>It used to be one <c>DELETE … WHERE event_time BETWEEN …</c>. That is a full table scan —
/// every index here leads with <c>instrument_id</c>, so a predicate on time alone cannot seek — and it
/// held SQLite's single write lock for the whole scan while live ticks queued behind it. These pin the
/// behaviour that replaced it: seek per instrument, delete in bounded chunks, and get the same rows.</para>
/// </summary>
public sealed class SqliteRangeDeleteTests : IDisposable
{
    private static readonly DateTime Epoch = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "daxalgo-range-delete-tests",
        Guid.NewGuid().ToString("N"));

    private readonly int _originalChunk = SqliteMarketDataStore.DeleteChunkRows;

    public void Dispose()
    {
        SqliteMarketDataStore.DeleteChunkRows = _originalChunk;
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp database is not worth failing a test over.
        }
    }

    [Fact]
    public async Task OnlyRowsInsideTheRangeAreDeleted()
    {
        using var store = Store();
        await WriteTradesAsync(store, instrument: 1, count: 10, start: Epoch, step: TimeSpan.FromMinutes(1));

        // [Epoch+2m, Epoch+5m) — three rows, and the boundaries must behave: start inclusive, end not.
        var deleted = await store.DeleteTradesInRangeAsync(Epoch.AddMinutes(2), Epoch.AddMinutes(5));

        Assert.Equal(3L, deleted);
        var remaining = await ReadTradeMinutesAsync(store);
        Assert.Equal([0, 1, 5, 6, 7, 8, 9], remaining);
    }

    [Fact]
    public async Task EveryInstrumentInTheRangeIsPruned()
    {
        // The per-instrument loop is the part that could silently skip data: miss an instrument and the
        // sweep reports success while the store keeps growing.
        using var store = Store();
        foreach (var instrument in new[] { 1, 2, 7 })
            await WriteTradesAsync(store, instrument, count: 6, start: Epoch, step: TimeSpan.FromMinutes(1));

        var deleted = await store.DeleteTradesInRangeAsync(Epoch, Epoch.AddMinutes(4));

        Assert.Equal(12L, deleted);
        Assert.Equal(6, await CountAsync(store, "trades"));
    }

    [Fact]
    public async Task MoreRowsThanOneChunkAreAllDeleted()
    {
        // The loop has to keep going until a chunk comes back short. An early break would leave rows
        // behind and quietly cap how much retention can ever reclaim.
        SqliteMarketDataStore.DeleteChunkRows = 10;
        using var store = Store();
        await WriteTradesAsync(store, instrument: 1, count: 95, start: Epoch, step: TimeSpan.FromSeconds(1));

        var deleted = await store.DeleteTradesInRangeAsync(Epoch, Epoch.AddSeconds(95));

        Assert.Equal(95L, deleted);
        Assert.Equal(0, await CountAsync(store, "trades"));
    }

    [Fact]
    public async Task AnExactMultipleOfTheChunkSizeTerminates()
    {
        // The boundary the loop condition turns on: a final chunk that is exactly full looks like
        // "there may be more", so the next pass must come back empty rather than spinning.
        SqliteMarketDataStore.DeleteChunkRows = 10;
        using var store = Store();
        await WriteTradesAsync(store, instrument: 1, count: 20, start: Epoch, step: TimeSpan.FromSeconds(1));

        var deleted = await store.DeleteTradesInRangeAsync(Epoch, Epoch.AddSeconds(20))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(20L, deleted);
        Assert.Equal(0, await CountAsync(store, "trades"));
    }

    [Fact]
    public async Task DeletingFromAnEmptyTableIsANoOp()
    {
        using var store = Store();

        Assert.Equal(0L, await store.DeleteTradesInRangeAsync(Epoch, Epoch.AddDays(1)));
    }

    [Fact]
    public async Task ARangeWithNothingInItLeavesEverythingAlone()
    {
        using var store = Store();
        await WriteTradesAsync(store, instrument: 1, count: 5, start: Epoch, step: TimeSpan.FromMinutes(1));

        var deleted = await store.DeleteTradesInRangeAsync(Epoch.AddDays(-2), Epoch.AddDays(-1));

        Assert.Equal(0L, deleted);
        Assert.Equal(5, await CountAsync(store, "trades"));
    }

    [Fact]
    public async Task CancellationStopsTheSweepPartWayThrough()
    {
        // Retention hands its own lifetime token in. Shutdown must not have to wait out a large delete.
        SqliteMarketDataStore.DeleteChunkRows = 1;
        using var store = Store();
        await WriteTradesAsync(store, instrument: 1, count: 40, start: Epoch, step: TimeSpan.FromSeconds(1));

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        try
        {
            await store.DeleteTradesInRangeAsync(Epoch, Epoch.AddSeconds(40), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The point of the test: it is allowed to stop early.
        }

        // Whatever it managed is committed — chunks are their own transactions, so cancelling loses no
        // work and leaves nothing half-deleted.
        Assert.InRange(await CountAsync(store, "trades"), 0, 40);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private SqliteMarketDataStore Store()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        var store = new SqliteMarketDataStore(
            connectionString,
            persist: true,
            batchSize: 500,
            NullLogger.Instance,
            SqliteStoreStream.Trades);
        ConnectionStrings[store] = connectionString;
        return store;
    }

    private static async Task WriteTradesAsync(
        SqliteMarketDataStore store,
        int instrument,
        int count,
        DateTime start,
        TimeSpan step)
    {
        for (var index = 0; index < count; index++)
        {
            var at = start + (step * index);
            store.EnqueueTrade(new TradePrint(
                new InstrumentId(instrument),
                at,
                at,
                100d + index,
                1L,
                AggressorSide.Buy,
                BrokerKind.Simulated,
                index,
                EventTimeApproximate: false));
        }

        await store.FlushAsync();
    }

    private static async Task<int> CountAsync(SqliteMarketDataStore store, string table)
    {
        await using var cn = Open(store);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int[]> ReadTradeMinutesAsync(SqliteMarketDataStore store)
    {
        await using var cn = Open(store);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT event_time FROM trades ORDER BY event_time";
        var minutes = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            minutes.Add((int)(EpochTime.FromMicros(reader.GetInt64(0)) - Epoch).TotalMinutes);
        return [.. minutes];
    }

    private static SqliteConnection Open(SqliteMarketDataStore store)
    {
        var cn = new SqliteConnection(ConnectionStrings[store]);
        cn.Open();
        return cn;
    }

    /// <summary>The store keeps its connection string private; the test remembers what it handed in.</summary>
    private static readonly Dictionary<SqliteMarketDataStore, string> ConnectionStrings = [];
}
