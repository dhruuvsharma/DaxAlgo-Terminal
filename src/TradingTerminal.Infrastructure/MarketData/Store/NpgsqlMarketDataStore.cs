using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// PostgreSQL/TimescaleDB-backed <see cref="IMarketDataStore"/> — the canonical store when the
/// Docker database is reachable. Uses Npgsql's built-in connection pooling: each batch and each
/// read borrows a pooled connection, so a container restart self-heals on the next operation.
/// Timestamps are native <c>TIMESTAMPTZ</c>; bar writes upsert on the <c>(instrument, size, time)</c> key.
/// </summary>
internal sealed class NpgsqlMarketDataStore : MarketDataStoreBase
{
    private readonly string _connectionString;

    public NpgsqlMarketDataStore(
        string connectionString, bool persist, int batchSize,
        int quoteRetentionDays, int tradeRetentionDays, int barRetentionDays,
        ILogger logger)
        : base(persist, batchSize, logger)
    {
        _connectionString = connectionString;
        using (var cn = new NpgsqlConnection(_connectionString))
        {
            cn.Open();
            TimescaleSchema.EnsureCreated(cn, logger);
            TimescaleSchema.ApplyRetention(cn, quoteRetentionDays, tradeRetentionDays, barRetentionDays, logger);
        }
        StartWriter();
    }

    protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
    {
        using var cn = new NpgsqlConnection(_connectionString);
        cn.Open();
        using var tx = cn.BeginTransaction();

        foreach (var op in batch)
        {
            switch (op.Kind)
            {
                case WriteKind.Quote: WriteQuote(cn, tx, op.Quote!); break;
                case WriteKind.Trade: WriteTrade(cn, tx, op.Trade!); break;
                case WriteKind.Bar: WriteBar(cn, tx, op.Bar!); break;
            }
        }

        tx.Commit();
    }

    private static void WriteQuote(NpgsqlConnection cn, NpgsqlTransaction tx, Quote q)
    {
        using var cmd = new NpgsqlCommand("""
            INSERT INTO quotes(instrument_id,event_time,ingest_time,bid,ask,bid_size,ask_size,source,seq,approx_time)
            VALUES(@i,@et,@it,@bid,@ask,@bs,@as,@src,@seq,@approx)
            """, cn, tx);
        cmd.Parameters.AddWithValue("i", q.InstrumentId.Value);
        cmd.Parameters.AddWithValue("et", TimescaleSchema.Utc(q.EventTimeUtc));
        cmd.Parameters.AddWithValue("it", TimescaleSchema.Utc(q.IngestTimeUtc));
        cmd.Parameters.AddWithValue("bid", q.Bid);
        cmd.Parameters.AddWithValue("ask", q.Ask);
        cmd.Parameters.AddWithValue("bs", q.BidSize);
        cmd.Parameters.AddWithValue("as", q.AskSize);
        cmd.Parameters.AddWithValue("src", (int)q.Source);
        cmd.Parameters.AddWithValue("seq", q.Sequence);
        cmd.Parameters.AddWithValue("approx", q.EventTimeApproximate);
        cmd.ExecuteNonQuery();
    }

    private static void WriteTrade(NpgsqlConnection cn, NpgsqlTransaction tx, TradePrint t)
    {
        using var cmd = new NpgsqlCommand("""
            INSERT INTO trades(instrument_id,event_time,ingest_time,price,size,aggressor,source,seq,approx_time)
            VALUES(@i,@et,@it,@px,@sz,@agg,@src,@seq,@approx)
            """, cn, tx);
        cmd.Parameters.AddWithValue("i", t.InstrumentId.Value);
        cmd.Parameters.AddWithValue("et", TimescaleSchema.Utc(t.EventTimeUtc));
        cmd.Parameters.AddWithValue("it", TimescaleSchema.Utc(t.IngestTimeUtc));
        cmd.Parameters.AddWithValue("px", t.Price);
        cmd.Parameters.AddWithValue("sz", t.Size);
        cmd.Parameters.AddWithValue("agg", (int)t.Aggressor);
        cmd.Parameters.AddWithValue("src", (int)t.Source);
        cmd.Parameters.AddWithValue("seq", t.Sequence);
        cmd.Parameters.AddWithValue("approx", t.EventTimeApproximate);
        cmd.ExecuteNonQuery();
    }

    private static void WriteBar(NpgsqlConnection cn, NpgsqlTransaction tx, OhlcvBar b)
    {
        using var cmd = new NpgsqlCommand("""
            INSERT INTO bars(instrument_id,bar_size,open_time,open,high,low,close,volume,source,is_final)
            VALUES(@i,@sz,@ot,@o,@h,@l,@c,@v,@src,@fin)
            ON CONFLICT(instrument_id,bar_size,open_time) DO UPDATE SET
                open=excluded.open, high=excluded.high, low=excluded.low,
                close=excluded.close, volume=excluded.volume, is_final=excluded.is_final
            """, cn, tx);
        cmd.Parameters.AddWithValue("i", b.InstrumentId.Value);
        cmd.Parameters.AddWithValue("sz", (int)b.Size);
        cmd.Parameters.AddWithValue("ot", TimescaleSchema.Utc(b.OpenTimeUtc));
        cmd.Parameters.AddWithValue("o", b.Open);
        cmd.Parameters.AddWithValue("h", b.High);
        cmd.Parameters.AddWithValue("l", b.Low);
        cmd.Parameters.AddWithValue("c", b.Close);
        cmd.Parameters.AddWithValue("v", b.Volume);
        cmd.Parameters.AddWithValue("src", (int)b.Source);
        cmd.Parameters.AddWithValue("fin", b.IsFinal);
        cmd.ExecuteNonQuery();
    }

    public override async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default)
    {
        await using var cn = new NpgsqlConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT open_time,open,high,low,close,volume,source,is_final
            FROM bars WHERE instrument_id=@i AND bar_size=@s
            ORDER BY open_time DESC LIMIT @n
            """, cn);
        cmd.Parameters.AddWithValue("i", instrumentId.Value);
        cmd.Parameters.AddWithValue("s", (int)size);
        cmd.Parameters.AddWithValue("n", Math.Max(1, count));

        var result = new List<OhlcvBar>(count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new OhlcvBar(
                instrumentId, size,
                rdr.GetFieldValue<DateTime>(0),
                rdr.GetDouble(1), rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetDouble(4),
                rdr.GetInt64(5), (Core.Brokers.BrokerKind)rdr.GetInt32(6), rdr.GetBoolean(7)));
        }
        result.Reverse();
        return result;
    }

    public override async IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new NpgsqlConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT event_time,ingest_time,bid,ask,bid_size,ask_size,source,seq,approx_time
            FROM quotes WHERE instrument_id=@i AND event_time>=@from AND event_time<@to
            ORDER BY event_time
            """, cn);
        cmd.Parameters.AddWithValue("i", instrumentId.Value);
        cmd.Parameters.AddWithValue("from", TimescaleSchema.Utc(fromUtc));
        cmd.Parameters.AddWithValue("to", TimescaleSchema.Utc(toUtc));

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new Quote(
                instrumentId,
                rdr.GetFieldValue<DateTime>(0), rdr.GetFieldValue<DateTime>(1),
                rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetInt64(4), rdr.GetInt64(5),
                (Core.Brokers.BrokerKind)rdr.GetInt32(6), rdr.GetInt64(7), rdr.GetBoolean(8));
        }
    }

    public override async IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new NpgsqlConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT event_time,ingest_time,price,size,aggressor,source,seq,approx_time
            FROM trades WHERE instrument_id=@i AND event_time>=@from AND event_time<@to
            ORDER BY event_time
            """, cn);
        cmd.Parameters.AddWithValue("i", instrumentId.Value);
        cmd.Parameters.AddWithValue("from", TimescaleSchema.Utc(fromUtc));
        cmd.Parameters.AddWithValue("to", TimescaleSchema.Utc(toUtc));

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new TradePrint(
                instrumentId,
                rdr.GetFieldValue<DateTime>(0), rdr.GetFieldValue<DateTime>(1),
                rdr.GetDouble(2), rdr.GetInt64(3), (AggressorSide)rdr.GetInt32(4),
                (Core.Brokers.BrokerKind)rdr.GetInt32(5), rdr.GetInt64(6), rdr.GetBoolean(7));
        }
    }
}
