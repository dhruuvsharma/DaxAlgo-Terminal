using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Npgsql;
using QuestDB;
using QuestDB.Senders;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// QuestDB-backed store for the high-volume L1/L2 streams — quotes, trades, and depth. Writes go
/// over the InfluxDB Line Protocol (HTTP), batched by the shared <see cref="MarketDataStoreBase"/>
/// writer thread: one <see cref="ISender"/> instance is touched only by that single thread, and
/// each batch is flushed with one <see cref="ISender.Send"/>. Reads use the PostgreSQL wire
/// protocol; values are inlined into the SQL (instrument is an int, times are framework-formatted)
/// because QuestDB's PG-wire prepared-parameter support is partial.
///
/// <para>Bars never reach this store — they stay in SQLite (see <see cref="CompositeMarketDataStore"/>).
/// Depth, which the SQLite/Postgres stores deliberately drop, is persisted here as one row per book
/// level: <c>(instrument, side, level, price, size)</c>, reconstructed into snapshots on read.</para>
/// </summary>
internal sealed class QuestDbMarketDataStore : MarketDataStoreBase
{
    private const string TsFormat = "yyyy-MM-ddTHH:mm:ss.ffffff";

    private readonly string _pgConnectionString;
    private readonly bool _available;
    private readonly ISender? _sender;

    /// <param name="available">False when QuestDB was unreachable at startup: the store is inert
    /// (no sender, no schema, persistence off) but still satisfies the interface so the app runs.</param>
    public QuestDbMarketDataStore(
        string ilpConfig, string pgConnectionString, bool persist, bool available,
        int batchSize, int depthRetentionDays, ILogger logger)
        : base(persist && available, batchSize, logger)
    {
        _pgConnectionString = pgConnectionString;
        _available = available;
        if (available)
        {
            QuestDbSchema.EnsureCreated(pgConnectionString, depthRetentionDays, logger);
            _sender = Sender.New(ilpConfig);
        }
        StartWriter();
    }

    protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
    {
        if (_sender is null) return; // inert (QuestDB unreachable)

        foreach (var op in batch)
        {
            switch (op.Kind)
            {
                case WriteKind.Quote: WriteQuote(_sender, op.Quote!); break;
                case WriteKind.Trade: WriteTrade(_sender, op.Trade!); break;
                case WriteKind.Depth: WriteDepth(_sender, op.Depth!); break;
                // Bars are routed to SQLite by the composite store and never arrive here.
            }
        }

        _sender.Send();
    }

    private static void WriteQuote(ISender s, Quote q) =>
        s.Table("quotes")
            .Symbol("instrument", q.InstrumentId.Value.ToString(CultureInfo.InvariantCulture))
            .Column("bid", q.Bid).Column("ask", q.Ask)
            .Column("bid_size", q.BidSize).Column("ask_size", q.AskSize)
            .Column("source", (long)(int)q.Source).Column("seq", q.Sequence)
            .Column("approx_time", q.EventTimeApproximate)
            .Column("ingest_time", EpochTime.ToMicros(q.IngestTimeUtc))
            .At(Utc(q.EventTimeUtc));

    private static void WriteTrade(ISender s, TradePrint t) =>
        s.Table("trades")
            .Symbol("instrument", t.InstrumentId.Value.ToString(CultureInfo.InvariantCulture))
            .Column("price", t.Price).Column("size", t.Size)
            .Column("aggressor", (long)(int)t.Aggressor)
            .Column("source", (long)(int)t.Source).Column("seq", t.Sequence)
            .Column("approx_time", t.EventTimeApproximate)
            .Column("ingest_time", EpochTime.ToMicros(t.IngestTimeUtc))
            .At(Utc(t.EventTimeUtc));

    private static void WriteDepth(ISender s, DepthRecord r)
    {
        var instrument = r.InstrumentId.Value.ToString(CultureInfo.InvariantCulture);
        var source = (long)(int)r.Source;
        var ingest = EpochTime.ToMicros(r.IngestTimeUtc);
        var ts = Utc(r.Snapshot.TimestampUtc);

        for (var i = 0; i < r.Snapshot.Bids.Count; i++)
            WriteLevel(s, instrument, "B", i, r.Snapshot.Bids[i], source, ingest, ts);
        for (var i = 0; i < r.Snapshot.Asks.Count; i++)
            WriteLevel(s, instrument, "A", i, r.Snapshot.Asks[i], source, ingest, ts);
    }

    private static void WriteLevel(
        ISender s, string instrument, string side, int level, DepthLevel lvl,
        long source, long ingest, DateTime ts) =>
        s.Table("depth")
            .Symbol("instrument", instrument).Symbol("side", side)
            .Column("level", (long)level)
            .Column("price", lvl.Price).Column("size", lvl.Size)
            .Column("source", source)
            .Column("ingest_time", ingest)
            .At(ts);

    public override Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OhlcvBar>>(Array.Empty<OhlcvBar>()); // bars live in SQLite, not QuestDB

    public override async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public override async IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_available) yield break;
        var sql = $"""
            SELECT ts, ingest_time, bid, ask, bid_size, ask_size, source, seq, approx_time
            FROM quotes WHERE instrument = '{instrumentId.Value}' {RangeClause(fromUtc, toUtc)}
            ORDER BY ts
            """;
        await foreach (var rdr in Query(sql, ct).ConfigureAwait(false))
            yield return new Quote(
                instrumentId,
                Utc(rdr.GetDateTime(0)), EpochTime.FromMicros(rdr.GetInt64(1)),
                rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetInt64(4), rdr.GetInt64(5),
                (BrokerKind)(int)rdr.GetInt64(6), rdr.GetInt64(7), rdr.GetBoolean(8));
    }

    public override async IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_available) yield break;
        var sql = $"""
            SELECT ts, ingest_time, price, size, aggressor, source, seq, approx_time
            FROM trades WHERE instrument = '{instrumentId.Value}' {RangeClause(fromUtc, toUtc)}
            ORDER BY ts
            """;
        await foreach (var rdr in Query(sql, ct).ConfigureAwait(false))
            yield return new TradePrint(
                instrumentId,
                Utc(rdr.GetDateTime(0)), EpochTime.FromMicros(rdr.GetInt64(1)),
                rdr.GetDouble(2), rdr.GetInt64(3), (AggressorSide)(int)rdr.GetInt64(4),
                (BrokerKind)(int)rdr.GetInt64(5), rdr.GetInt64(6), rdr.GetBoolean(7));
    }

    public override async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_available) yield break;
        // Rows are flattened levels; ascending ts (then bids-before-asks by level) lets us
        // re-group consecutive rows that share a timestamp back into one snapshot.
        var sql = $"""
            SELECT ts, side, level, price, size
            FROM depth WHERE instrument = '{instrumentId.Value}' {RangeClause(fromUtc, toUtc)}
            ORDER BY ts, side DESC, level
            """;

        DateTime? currentTs = null;
        var bids = new List<DepthLevel>();
        var asks = new List<DepthLevel>();

        await foreach (var rdr in Query(sql, ct).ConfigureAwait(false))
        {
            var ts = Utc(rdr.GetDateTime(0));
            if (currentTs is { } cur && ts != cur)
            {
                yield return new DepthSnapshot(cur, bids.ToArray(), asks.ToArray());
                bids = new List<DepthLevel>();
                asks = new List<DepthLevel>();
            }
            currentTs = ts;

            var level = new DepthLevel(rdr.GetDouble(3), rdr.GetInt64(4));
            if (rdr.GetString(1) == "B") bids.Add(level); else asks.Add(level);
        }

        if (currentTs is { } last)
            yield return new DepthSnapshot(last, bids.ToArray(), asks.ToArray());
    }

    public override Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        DropPartitionsAsync("quotes", fromUtc, toUtc, ct);

    public override Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        DropPartitionsAsync("trades", fromUtc, toUtc, ct);

    public override Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        DropPartitionsAsync("depth", fromUtc, toUtc, ct);

    public override Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult(0L); // bars live in SQLite

    /// <summary>QuestDB has no row-level DELETE; the archiver's "prune what was offloaded" maps to
    /// dropping whole day-partitions. The range predicate (verified) only drops partitions whose
    /// data is fully inside [from, to), so it never deletes older un-archived data — boundary
    /// partitions straddling an edge are left intact. Returns -1 (exact row count unknown).</summary>
    private async Task<long> DropPartitionsAsync(string table, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (!_available) return 0;
        try
        {
            var from = Utc(fromUtc).ToString(TsFormat, CultureInfo.InvariantCulture);
            var to = Utc(toUtc).ToString(TsFormat, CultureInfo.InvariantCulture);
            await using var cn = new NpgsqlConnection(_pgConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                $"ALTER TABLE {table} DROP PARTITION WHERE ts >= '{from}' AND ts < '{to}'", cn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return -1;
        }
        catch (Exception)
        {
            return 0; // no whole partition inside the range, or table empty — nothing dropped
        }
    }

    private async IAsyncEnumerable<NpgsqlDataReader> Query(string sql, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var cn = new NpgsqlConnection(_pgConnectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            yield return rdr;
    }

    private static string RangeClause(DateTime fromUtc, DateTime toUtc) =>
        $"AND ts >= '{Utc(fromUtc).ToString(TsFormat, CultureInfo.InvariantCulture)}' " +
        $"AND ts < '{Utc(toUtc).ToString(TsFormat, CultureInfo.InvariantCulture)}'";

    private static DateTime Utc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt
        : dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime()
        : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    protected override void OnDispose() => _sender?.Dispose();
}
