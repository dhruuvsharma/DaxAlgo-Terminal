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
    /// <summary>
    /// The columns that identify a bar, and therefore the upsert-dedup key set.
    ///
    /// <para>QuestDB requires the designated timestamp to be one of the keys, which suits a bar
    /// exactly: its open time IS its identity. A re-sent forming bar replaces its row instead of
    /// appending — the same contract SQLite got from ON CONFLICT DO UPDATE.</para>
    /// </summary>
    internal const string BarDedupKeys = "ts, instrument, bar_size";
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

        // Bars are the one stream that is REWRITTEN rather than appended: a forming bar is re-sent on
        // every update until it closes. ILP has no update, so without dedup each tick of a live bar
        // would land as another row and a day of 1-minute bars would read back thousands deep.
        //
        // DEDUP UPSERT KEYS makes a repeat of (ts, instrument, bar_size) replace the row instead of
        // adding one — the same contract SQLite got from ON CONFLICT DO UPDATE. The designated
        // timestamp must be one of the keys, which suits a bar exactly: its open time IS its identity.
        Execute(cn, """
            CREATE TABLE IF NOT EXISTS bars (
                instrument SYMBOL,
                bar_size SYMBOL,
                open DOUBLE, high DOUBLE, low DOUBLE, close DOUBLE,
                volume LONG,
                source LONG,
                is_final BOOLEAN,
                ts TIMESTAMP
            ) TIMESTAMP(ts) PARTITION BY DAY WAL
            """ + $"DEDUP UPSERT KEYS({BarDedupKeys});");

        // An existing table from before bars moved here will not have dedup enabled; turning it on is
        // idempotent and cheap, and without it the rewrite-per-update above silently duplicates.
        TryEnableDedup(cn, "bars", BarDedupKeys, logger);
        // TTL is supported on newer QuestDB builds only; treat failure as "keep forever".
        if (depthRetentionDays > 0)
            TryApplyTtl(cn, "depth", depthRetentionDays, logger);

        logger.LogInformation("QuestDB schema ready (quotes, trades, depth, bars).");
    }

    private static void Execute(NpgsqlConnection cn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, cn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Enables upsert-dedup on an existing table. Already-enabled is not an error.</summary>
    private static void TryEnableDedup(NpgsqlConnection cn, string table, string keys, ILogger logger)
    {
        try { Execute(cn, $"ALTER TABLE {table} DEDUP ENABLE UPSERT KEYS({keys});"); }
        catch (Exception ex) { logger.LogDebug(ex, "QuestDB dedup not applied for {Table}", table); }
    }
    private static void TryApplyTtl(NpgsqlConnection cn, string table, int days, ILogger logger)
    {
        try { Execute(cn, $"ALTER TABLE {table} SET TTL {days} DAYS;"); }
        catch (Exception ex) { logger.LogDebug(ex, "QuestDB TTL not applied for {Table} (build may not support SET TTL)", table); }
    }
}
