using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// SQLite-backed <see cref="IMarketDataStore"/> — the embedded, zero-config store used when no
/// containerized database is reachable. One write connection (touched only by the base class's
/// writer thread) plus short-lived pooled read connections; WAL mode lets reads run concurrently
/// with the writer. Timestamps are epoch microseconds (see <see cref="EpochTime"/>).
/// </summary>
internal sealed class SqliteMarketDataStore : MarketDataStoreBase
{
    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;
    private readonly SqliteStoreStream _stream;

    /// <param name="stream">Which stream this file owns. <see cref="SqliteStoreStream.All"/> (default)
    /// creates the identity registry + quotes/trades/bars (the single-file backend). A single-stream
    /// value creates only that table — used by the per-broker backend's one-file-per-stream layout.
    /// Only a <see cref="SqliteStoreStream.Depth"/> store persists/serves depth.</param>
    /// <param name="depthRetentionDays">For a <see cref="SqliteStoreStream.Depth"/> store, prune depth
    /// rows older than this many days on open (0 = keep forever). Best-effort startup prune; depth is
    /// the highest-volume stream, so this bounds the <c>…-l2.db</c> file across restarts.</param>
    public SqliteMarketDataStore(
        string connectionString, bool persist, int batchSize, ILogger logger,
        SqliteStoreStream stream = SqliteStoreStream.All, int depthRetentionDays = 0)
        : base(persist, batchSize, logger)
    {
        _connectionString = connectionString;
        _stream = stream;
        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();
        SqliteSchema.ApplyPragmas(_writeConnection);
        switch (stream)
        {
            case SqliteStoreStream.Quotes: SqliteSchema.EnsureQuotesCreated(_writeConnection); break;
            case SqliteStoreStream.Trades: SqliteSchema.EnsureTradesCreated(_writeConnection); break;
            case SqliteStoreStream.Bars:   SqliteSchema.EnsureBarsCreated(_writeConnection); break;
            case SqliteStoreStream.Depth:  SqliteSchema.EnsureDepthCreated(_writeConnection); break;
            default:                       SqliteSchema.EnsureCreated(_writeConnection); break;
        }
        if (stream == SqliteStoreStream.Depth && depthRetentionDays > 0)
            PruneDepthOlderThan(DateTime.UtcNow.AddDays(-depthRetentionDays));
        StartWriter();
    }

    private void PruneDepthOlderThan(DateTime cutoffUtc)
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = "DELETE FROM depth WHERE event_time < $cut";
        cmd.Parameters.AddWithValue("$cut", EpochTime.ToMicros(cutoffUtc));
        cmd.ExecuteNonQuery();
    }

    protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
    {
        using var tx = _writeConnection.BeginTransaction();

        // Commands are created lazily per kind so a single-stream file only prepares the one
        // statement it owns (and never references a table it didn't create).
        SqliteCommand? quoteCmd = null, tradeCmd = null, barCmd = null, depthCmd = null;
        try
        {
            foreach (var op in batch)
            {
                switch (op.Kind)
                {
                    case WriteKind.Quote:
                        BindQuote(quoteCmd ??= CreateQuoteCmd(tx), op.Quote!); quoteCmd.ExecuteNonQuery(); break;
                    case WriteKind.Trade:
                        BindTrade(tradeCmd ??= CreateTradeCmd(tx), op.Trade!); tradeCmd.ExecuteNonQuery(); break;
                    case WriteKind.Bar:
                        BindBar(barCmd ??= CreateBarCmd(tx), op.Bar!); barCmd.ExecuteNonQuery(); break;
                    case WriteKind.Depth:
                        WriteDepth(depthCmd ??= CreateDepthCmd(tx), op.Depth!); break;
                }
            }
            tx.Commit();
        }
        finally
        {
            quoteCmd?.Dispose(); tradeCmd?.Dispose(); barCmd?.Dispose(); depthCmd?.Dispose();
        }
    }

    private SqliteCommand CreateQuoteCmd(SqliteTransaction tx)
    {
        var cmd = _writeConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO quotes(instrument_id,event_time,ingest_time,bid,ask,bid_size,ask_size,source,seq,approx_time)
            VALUES($i,$et,$it,$bid,$ask,$bs,$as,$src,$seq,$approx)
            """;
        foreach (var p in new[] { "$i", "$et", "$it", "$bid", "$ask", "$bs", "$as", "$src", "$seq", "$approx" })
            cmd.Parameters.Add(cmd.CreateParameter()).ParameterName = p;
        return cmd;
    }

    private static void BindQuote(SqliteCommand cmd, Quote q)
    {
        cmd.Parameters["$i"].Value = q.InstrumentId.Value;
        cmd.Parameters["$et"].Value = EpochTime.ToMicros(q.EventTimeUtc);
        cmd.Parameters["$it"].Value = EpochTime.ToMicros(q.IngestTimeUtc);
        cmd.Parameters["$bid"].Value = q.Bid;
        cmd.Parameters["$ask"].Value = q.Ask;
        cmd.Parameters["$bs"].Value = q.BidSize;
        cmd.Parameters["$as"].Value = q.AskSize;
        cmd.Parameters["$src"].Value = (int)q.Source;
        cmd.Parameters["$seq"].Value = q.Sequence;
        cmd.Parameters["$approx"].Value = q.EventTimeApproximate ? 1 : 0;
    }

    private SqliteCommand CreateTradeCmd(SqliteTransaction tx)
    {
        var cmd = _writeConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO trades(instrument_id,event_time,ingest_time,price,size,aggressor,source,seq,approx_time)
            VALUES($i,$et,$it,$px,$sz,$agg,$src,$seq,$approx)
            """;
        foreach (var p in new[] { "$i", "$et", "$it", "$px", "$sz", "$agg", "$src", "$seq", "$approx" })
            cmd.Parameters.Add(cmd.CreateParameter()).ParameterName = p;
        return cmd;
    }

    private static void BindTrade(SqliteCommand cmd, TradePrint t)
    {
        cmd.Parameters["$i"].Value = t.InstrumentId.Value;
        cmd.Parameters["$et"].Value = EpochTime.ToMicros(t.EventTimeUtc);
        cmd.Parameters["$it"].Value = EpochTime.ToMicros(t.IngestTimeUtc);
        cmd.Parameters["$px"].Value = t.Price;
        cmd.Parameters["$sz"].Value = t.Size;
        cmd.Parameters["$agg"].Value = (int)t.Aggressor;
        cmd.Parameters["$src"].Value = (int)t.Source;
        cmd.Parameters["$seq"].Value = t.Sequence;
        cmd.Parameters["$approx"].Value = t.EventTimeApproximate ? 1 : 0;
    }

    private SqliteCommand CreateBarCmd(SqliteTransaction tx)
    {
        var cmd = _writeConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO bars(instrument_id,bar_size,open_time,open,high,low,close,volume,source,is_final)
            VALUES($i,$sz,$ot,$o,$h,$l,$c,$v,$src,$fin)
            ON CONFLICT(instrument_id,bar_size,open_time) DO UPDATE SET
                open=excluded.open, high=excluded.high, low=excluded.low,
                close=excluded.close, volume=excluded.volume, is_final=excluded.is_final
            """;
        foreach (var p in new[] { "$i", "$sz", "$ot", "$o", "$h", "$l", "$c", "$v", "$src", "$fin" })
            cmd.Parameters.Add(cmd.CreateParameter()).ParameterName = p;
        return cmd;
    }

    private static void BindBar(SqliteCommand cmd, OhlcvBar b)
    {
        cmd.Parameters["$i"].Value = b.InstrumentId.Value;
        cmd.Parameters["$sz"].Value = (int)b.Size;
        cmd.Parameters["$ot"].Value = EpochTime.ToMicros(b.OpenTimeUtc);
        cmd.Parameters["$o"].Value = b.Open;
        cmd.Parameters["$h"].Value = b.High;
        cmd.Parameters["$l"].Value = b.Low;
        cmd.Parameters["$c"].Value = b.Close;
        cmd.Parameters["$v"].Value = b.Volume;
        cmd.Parameters["$src"].Value = (int)b.Source;
        cmd.Parameters["$fin"].Value = b.IsFinal ? 1 : 0;
    }

    private SqliteCommand CreateDepthCmd(SqliteTransaction tx)
    {
        var cmd = _writeConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO depth(instrument_id,event_time,ingest_time,side,level,price,size,source)
            VALUES($i,$et,$it,$side,$lvl,$px,$sz,$src)
            """;
        foreach (var p in new[] { "$i", "$et", "$it", "$side", "$lvl", "$px", "$sz", "$src" })
            cmd.Parameters.Add(cmd.CreateParameter()).ParameterName = p;
        return cmd;
    }

    // One DepthRecord → one row per book level (side 0 = bid, 1 = ask), regrouped on read.
    private static void WriteDepth(SqliteCommand cmd, DepthRecord r)
    {
        var et = EpochTime.ToMicros(r.Snapshot.TimestampUtc);
        var it = EpochTime.ToMicros(r.IngestTimeUtc);
        for (var i = 0; i < r.Snapshot.Bids.Count; i++)
            WriteDepthLevel(cmd, r.InstrumentId, et, it, side: 0, level: i, r.Snapshot.Bids[i], r.Source);
        for (var i = 0; i < r.Snapshot.Asks.Count; i++)
            WriteDepthLevel(cmd, r.InstrumentId, et, it, side: 1, level: i, r.Snapshot.Asks[i], r.Source);
    }

    private static void WriteDepthLevel(
        SqliteCommand cmd, InstrumentId id, long et, long it, int side, int level, DepthLevel lvl, BrokerKind source)
    {
        cmd.Parameters["$i"].Value = id.Value;
        cmd.Parameters["$et"].Value = et;
        cmd.Parameters["$it"].Value = it;
        cmd.Parameters["$side"].Value = side;
        cmd.Parameters["$lvl"].Value = level;
        cmd.Parameters["$px"].Value = lvl.Price;
        cmd.Parameters["$sz"].Value = lvl.Size;
        cmd.Parameters["$src"].Value = (int)source;
        cmd.ExecuteNonQuery();
    }

    public override async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        // Only union the tables this store actually owns, so a single-stream file doesn't query a
        // table it never created.
        cmd.CommandText = _stream switch
        {
            SqliteStoreStream.Quotes => "SELECT MIN(event_time), MAX(event_time) FROM quotes",
            SqliteStoreStream.Trades => "SELECT MIN(event_time), MAX(event_time) FROM trades",
            SqliteStoreStream.Bars   => "SELECT MIN(open_time),  MAX(open_time)  FROM bars",
            SqliteStoreStream.Depth  => "SELECT MIN(event_time), MAX(event_time) FROM depth",
            _ => """
                SELECT MIN(mn), MAX(mx) FROM (
                    SELECT MIN(event_time) mn, MAX(event_time) mx FROM quotes
                    UNION ALL SELECT MIN(event_time), MAX(event_time) FROM trades
                    UNION ALL SELECT MIN(open_time),  MAX(open_time)  FROM bars
                )
                """,
        };
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await rdr.ReadAsync(ct).ConfigureAwait(false) || rdr.IsDBNull(0) || rdr.IsDBNull(1))
            return StoredDataExtent.Empty;
        return new StoredDataExtent(EpochTime.FromMicros(rdr.GetInt64(0)), EpochTime.FromMicros(rdr.GetInt64(1)));
    }

    public override async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, BrokerKind? source = null,
        CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            SELECT open_time,open,high,low,close,volume,source,is_final
            FROM bars WHERE instrument_id=$i AND bar_size=$s{SourceClause(source)}
            ORDER BY open_time DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$s", (int)size);
        cmd.Parameters.AddWithValue("$n", Math.Max(1, count));
        BindSource(cmd, source);

        var result = new List<OhlcvBar>(count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new OhlcvBar(
                instrumentId, size,
                EpochTime.FromMicros(rdr.GetInt64(0)),
                rdr.GetDouble(1), rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetDouble(4),
                rdr.GetInt64(5), (Core.Brokers.BrokerKind)rdr.GetInt32(6), rdr.GetInt32(7) != 0));
        }
        result.Reverse();
        return result;
    }

    public override async IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            SELECT event_time,ingest_time,bid,ask,bid_size,ask_size,source,seq,approx_time
            FROM quotes WHERE instrument_id=$i AND event_time>=$from AND event_time<$to{SourceClause(source)}
            ORDER BY event_time
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));
        BindSource(cmd, source);

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new Quote(
                instrumentId,
                EpochTime.FromMicros(rdr.GetInt64(0)),
                EpochTime.FromMicros(rdr.GetInt64(1)),
                rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetInt64(4), rdr.GetInt64(5),
                (Core.Brokers.BrokerKind)rdr.GetInt32(6), rdr.GetInt64(7), rdr.GetInt32(8) != 0);
        }
    }

    public override async IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            SELECT event_time,ingest_time,price,size,aggressor,source,seq,approx_time
            FROM trades WHERE instrument_id=$i AND event_time>=$from AND event_time<$to{SourceClause(source)}
            ORDER BY event_time
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));
        BindSource(cmd, source);

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new TradePrint(
                instrumentId,
                EpochTime.FromMicros(rdr.GetInt64(0)),
                EpochTime.FromMicros(rdr.GetInt64(1)),
                rdr.GetDouble(2), rdr.GetInt64(3), (AggressorSide)rdr.GetInt32(4),
                (Core.Brokers.BrokerKind)rdr.GetInt32(5), rdr.GetInt64(6), rdr.GetInt32(7) != 0);
        }
    }

    public override async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
        BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            SELECT open_time,open,high,low,close,volume,source,is_final
            FROM bars WHERE instrument_id=$i AND bar_size=$s AND open_time>=$from AND open_time<$to{SourceClause(source)}
            ORDER BY open_time
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$s", (int)size);
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));
        BindSource(cmd, source);

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new OhlcvBar(
                instrumentId, size,
                EpochTime.FromMicros(rdr.GetInt64(0)),
                rdr.GetDouble(1), rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetDouble(4),
                rdr.GetInt64(5), (Core.Brokers.BrokerKind)rdr.GetInt32(6), rdr.GetInt32(7) != 0);
        }
    }

    /// <summary>Reconstruct depth snapshots from the flattened level rows. Only a
    /// <see cref="SqliteStoreStream.Depth"/> store has the table; others yield nothing.</summary>
    public override async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_stream != SqliteStoreStream.Depth) { await Task.CompletedTask.ConfigureAwait(false); yield break; }

        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT event_time,side,level,price,size
            FROM depth WHERE instrument_id=$i AND event_time>=$from AND event_time<$to
            ORDER BY event_time, side, level
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long? curMicros = null;
        var bids = new List<DepthLevel>();
        var asks = new List<DepthLevel>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var et = rdr.GetInt64(0);
            if (curMicros is { } prev && et != prev)
            {
                yield return new DepthSnapshot(EpochTime.FromMicros(prev), bids.ToArray(), asks.ToArray());
                bids = new List<DepthLevel>();
                asks = new List<DepthLevel>();
            }
            curMicros = et;
            var lvl = new DepthLevel(rdr.GetDouble(3), rdr.GetInt64(4));
            if (rdr.GetInt32(1) == 0) bids.Add(lvl); else asks.Add(lvl);
        }
        if (curMicros is { } last)
            yield return new DepthSnapshot(EpochTime.FromMicros(last), bids.ToArray(), asks.ToArray());
    }

    public override Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        DeleteInRangeAsync("quotes", "event_time", fromUtc, toUtc, ct);

    public override Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        DeleteInRangeAsync("trades", "event_time", fromUtc, toUtc, ct);

    public override Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        DeleteInRangeAsync("bars", "open_time", fromUtc, toUtc, ct);

    public override Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        _stream == SqliteStoreStream.Depth
            ? DeleteInRangeAsync("depth", "event_time", fromUtc, toUtc, ct)
            : Task.FromResult(0L);

    private async Task<long> DeleteInRangeAsync(string table, string timeCol, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE {timeCol} >= $from AND {timeCol} < $to";
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── Optional source (broker) read filter ────────────────────────────────────────────────
    // null = all brokers merged (legacy/archive); set = only that broker's rows. The single-file
    // backend honours it on reads; the per-broker backend uses it to pick the file (and still
    // passes it down so a stray cross-broker row can't slip through).
    private static string SourceClause(BrokerKind? source) => source is null ? "" : " AND source=$src";

    private static void BindSource(SqliteCommand cmd, BrokerKind? source)
    {
        if (source is { } b) cmd.Parameters.AddWithValue("$src", (int)b);
    }

    protected override void OnDispose() => _writeConnection.Dispose();
}
