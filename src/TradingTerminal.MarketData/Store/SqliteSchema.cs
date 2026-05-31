using Microsoft.Data.Sqlite;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Creates (idempotently) the canonical market-data schema and applies the connection-level
/// pragmas the pipeline relies on. Shared by the store and the instrument registry, which both
/// point at the same database file. Timestamps are stored as epoch microseconds UTC (see
/// <see cref="EpochTime"/>) so the format is timezone-agnostic and integer-comparable.
/// </summary>
internal static class SqliteSchema
{
    /// <summary>WAL + a busy timeout so the single background writer and short-lived reader
    /// connections don't trip "database is locked". Apply on every opened connection.</summary>
    public static void ApplyPragmas(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();
    }

    public static void EnsureCreated(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS instruments (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                canonical_symbol TEXT NOT NULL,
                asset_class     INTEGER NOT NULL,
                exchange        TEXT NOT NULL DEFAULT '',
                currency        TEXT NOT NULL DEFAULT 'USD',
                tick_size       REAL NOT NULL DEFAULT 0.01,
                multiplier      REAL NOT NULL DEFAULT 1.0,
                UNIQUE(canonical_symbol, asset_class, exchange)
            );

            CREATE TABLE IF NOT EXISTS instrument_aliases (
                broker          INTEGER NOT NULL,
                broker_symbol   TEXT NOT NULL,
                broker_native_id TEXT,
                instrument_id   INTEGER NOT NULL REFERENCES instruments(id),
                PRIMARY KEY (broker, broker_symbol)
            );

            CREATE TABLE IF NOT EXISTS quotes (
                instrument_id   INTEGER NOT NULL,
                event_time      INTEGER NOT NULL,
                ingest_time     INTEGER NOT NULL,
                bid             REAL NOT NULL,
                ask             REAL NOT NULL,
                bid_size        INTEGER NOT NULL,
                ask_size        INTEGER NOT NULL,
                source          INTEGER NOT NULL,
                seq             INTEGER NOT NULL,
                approx_time     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_quotes_instr_time ON quotes(instrument_id, event_time);

            CREATE TABLE IF NOT EXISTS trades (
                instrument_id   INTEGER NOT NULL,
                event_time      INTEGER NOT NULL,
                ingest_time     INTEGER NOT NULL,
                price           REAL NOT NULL,
                size            INTEGER NOT NULL,
                aggressor       INTEGER NOT NULL,
                source          INTEGER NOT NULL,
                seq             INTEGER NOT NULL,
                approx_time     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_trades_instr_time ON trades(instrument_id, event_time);

            CREATE TABLE IF NOT EXISTS bars (
                instrument_id   INTEGER NOT NULL,
                bar_size        INTEGER NOT NULL,
                open_time       INTEGER NOT NULL,
                open            REAL NOT NULL,
                high            REAL NOT NULL,
                low             REAL NOT NULL,
                close           REAL NOT NULL,
                volume          INTEGER NOT NULL,
                source          INTEGER NOT NULL,
                is_final        INTEGER NOT NULL,
                PRIMARY KEY (instrument_id, bar_size, open_time)
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
