using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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

    public SqliteMarketDataStore(string connectionString, bool persist, int batchSize, ILogger logger)
        : base(persist, batchSize, logger)
    {
        _connectionString = connectionString;
        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();
        SqliteSchema.ApplyPragmas(_writeConnection);
        SqliteSchema.EnsureCreated(_writeConnection);
        StartWriter();
    }

    protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
    {
        using var tx = _writeConnection.BeginTransaction();
        using var quoteCmd = CreateQuoteCmd(tx);
        using var tradeCmd = CreateTradeCmd(tx);
        using var barCmd = CreateBarCmd(tx);

        foreach (var op in batch)
        {
            switch (op.Kind)
            {
                case WriteKind.Quote: BindQuote(quoteCmd, op.Quote!); quoteCmd.ExecuteNonQuery(); break;
                case WriteKind.Trade: BindTrade(tradeCmd, op.Trade!); tradeCmd.ExecuteNonQuery(); break;
                case WriteKind.Bar: BindBar(barCmd, op.Bar!); barCmd.ExecuteNonQuery(); break;
            }
        }

        tx.Commit();
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

    public override async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT open_time,open,high,low,close,volume,source,is_final
            FROM bars WHERE instrument_id=$i AND bar_size=$s
            ORDER BY open_time DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$s", (int)size);
        cmd.Parameters.AddWithValue("$n", Math.Max(1, count));

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
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT event_time,ingest_time,bid,ask,bid_size,ask_size,source,seq,approx_time
            FROM quotes WHERE instrument_id=$i AND event_time>=$from AND event_time<$to
            ORDER BY event_time
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));

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
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT event_time,ingest_time,price,size,aggressor,source,seq,approx_time
            FROM trades WHERE instrument_id=$i AND event_time>=$from AND event_time<$to
            ORDER BY event_time
            """;
        cmd.Parameters.AddWithValue("$i", instrumentId.Value);
        cmd.Parameters.AddWithValue("$from", EpochTime.ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", EpochTime.ToMicros(toUtc));

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

    protected override void OnDispose() => _writeConnection.Dispose();
}
