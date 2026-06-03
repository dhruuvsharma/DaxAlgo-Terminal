using Microsoft.Extensions.Logging;
using Npgsql;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Creates the QuestDB tables for the L1/L2 streams over the PostgreSQL wire protocol (port 8812)
/// and applies a best-effort partition TTL. Column types are chosen to match exactly what the
/// InfluxDB Line Protocol writer emits — QuestDB's ILP only has a single 64-bit integer type, so
/// every integer column is <c>LONG</c> (not <c>INT</c>) to avoid a type clash on first ingest.
/// Tables are WAL + <c>PARTITION BY DAY</c>, the layout QuestDB wants for high-cardinality
/// time-series with retention.
/// </summary>
internal static class QuestDbSchema
{
    public static void EnsureCreated(string pgConnectionString, int depthRetentionDays, ILogger logger)
    {
        using var cn = new NpgsqlConnection(pgConnectionString);
        cn.Open();

        Execute(cn, """
            CREATE TABLE IF NOT EXISTS quotes (
                instrument SYMBOL,
                bid DOUBLE, ask DOUBLE,
                bid_size LONG, ask_size LONG,
                source LONG, seq LONG,
                approx_time BOOLEAN,
                ingest_time LONG,
                ts TIMESTAMP
            ) TIMESTAMP(ts) PARTITION BY DAY WAL;
            """);

        Execute(cn, """
            CREATE TABLE IF NOT EXISTS trades (
                instrument SYMBOL,
                price DOUBLE, size LONG,
                aggressor LONG, source LONG, seq LONG,
                approx_time BOOLEAN,
                ingest_time LONG,
                ts TIMESTAMP
            ) TIMESTAMP(ts) PARTITION BY DAY WAL;
            """);

        Execute(cn, """
            CREATE TABLE IF NOT EXISTS depth (
                instrument SYMBOL,
                side SYMBOL,
                level LONG,
                price DOUBLE, size LONG,
                source LONG,
                ingest_time LONG,
                ts TIMESTAMP
            ) TIMESTAMP(ts) PARTITION BY DAY WAL;
            """);

        // TTL is supported on newer QuestDB builds only; treat failure as "keep forever".
        if (depthRetentionDays > 0)
            TryApplyTtl(cn, "depth", depthRetentionDays, logger);

        logger.LogInformation("QuestDB schema ready (quotes, trades, depth).");
    }

    private static void Execute(NpgsqlConnection cn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, cn);
        cmd.ExecuteNonQuery();
    }

    private static void TryApplyTtl(NpgsqlConnection cn, string table, int days, ILogger logger)
    {
        try { Execute(cn, $"ALTER TABLE {table} SET TTL {days} DAYS;"); }
        catch (Exception ex) { logger.LogDebug(ex, "QuestDB TTL not applied for {Table} (build may not support SET TTL)", table); }
    }
}
