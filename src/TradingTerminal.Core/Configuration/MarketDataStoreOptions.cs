namespace TradingTerminal.Core.Configuration;

/// <summary>Which backend persists the canonical market-data store.</summary>
public enum MarketDataProvider
{
    /// <summary>Embedded SQLite file — zero-config, always available.</summary>
    Sqlite = 0,

    /// <summary>PostgreSQL/TimescaleDB over the network (e.g. the docker-compose service). Falls back
    /// to SQLite automatically when the database can't be reached at startup.</summary>
    Postgres = 1,

    /// <summary>Split backend: high-volume L1/L2 (quotes, trades, depth) go to QuestDB over ILP/PG-wire;
    /// bars stay in the embedded SQLite store. Unlike <see cref="Postgres"/> there is <b>no silent
    /// fallback</b> — when QuestDB is configured but unreachable, tick/depth persistence is disabled
    /// (logged loudly) rather than diverted to SQLite. Bars are unaffected.</summary>
    QuestDb = 2,
}

/// <summary>
/// Settings for the local market-data pipeline (canonical store + ingest). Two backends:
/// embedded SQLite (default, zero-config) and PostgreSQL/TimescaleDB (the docker-compose service).
/// When <see cref="Provider"/> is <see cref="MarketDataProvider.Postgres"/> but the database is
/// unreachable at startup, the pipeline transparently falls back to SQLite so the app still runs.
/// </summary>
public sealed class MarketDataStoreOptions
{
    public const string SectionName = "MarketDataStore";

    /// <summary>Master switch for the persistence + ingest pipeline.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which storage backend to use (with auto-fallback to SQLite for Postgres).</summary>
    public MarketDataProvider Provider { get; set; } = MarketDataProvider.Sqlite;

    /// <summary>PostgreSQL/TimescaleDB connection string. Defaults to the docker-compose service
    /// (localhost:5432, db/user/pass = daxalgo). Only used when <see cref="Provider"/> is Postgres.</summary>
    public string PostgresConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=daxalgo;Username=daxalgo;Password=daxalgo;Timeout=5;Command Timeout=10";

    /// <summary>SQLite database file path. Empty → a default under the app's local data folder.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Persist normalized records as they stream. When false the live hub still works
    /// (in-memory only) but nothing is written to disk.</summary>
    public bool PersistLiveData { get; set; } = true;

    /// <summary>Max records buffered before the background writer forces a flush.</summary>
    public int WriteBatchSize { get; set; } = 500;

    /// <summary>Max time a record waits in the buffer before a flush, even if the batch isn't full.</summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>
    /// TimescaleDB retention windows per hypertable, in days. 0 or negative = keep forever (no
    /// retention policy created). Quotes dominate volume so the default trims them aggressively;
    /// bars are tiny so we keep them indefinitely. SQLite ignores all three (no native retention).
    /// </summary>
    public int QuoteRetentionDays { get; set; } = 30;

    /// <summary>Trade-print retention in days. 0 or negative = keep forever.</summary>
    public int TradeRetentionDays { get; set; } = 30;

    /// <summary>OHLCV-bar retention in days. 0 or negative (default) = keep forever — bars are small
    /// and the historical-cache value compounds over time.</summary>
    public int BarRetentionDays { get; set; } = 0;

    // ── QuestDB (Provider == QuestDb) ────────────────────────────────────────────────────────
    // QuestDB is a standalone time-series server (see the docker-compose `questdb` service), not
    // embeddable. Writes use the InfluxDB Line Protocol over HTTP (port 9000); reads use the
    // PostgreSQL wire protocol (port 8812) via Npgsql. Bars continue to use the SQLite store; only
    // the high-volume L1/L2 streams land in QuestDB.

    /// <summary>QuestDB ILP client configuration string (HTTP transport). <c>auto_flush=off</c> keeps
    /// flushing deterministic — the batched background writer calls <c>Send()</c> once per batch.</summary>
    public string QuestDbIlpConfig { get; set; } = "http::addr=localhost:9000;auto_flush=off;";

    /// <summary>QuestDB PG-wire connection string used for schema creation and replay/research reads.
    /// Defaults to QuestDB's out-of-the-box credentials (admin/quest, db <c>qdb</c>, port 8812).</summary>
    public string QuestDbPgConnectionString { get; set; } =
        "Host=localhost;Port=8812;Database=qdb;Username=admin;Password=quest;Timeout=5;Command Timeout=15;ServerCompatibilityMode=NoTypeLoading";

    /// <summary>Depth (L2) snapshot retention in days, applied as a QuestDB partition TTL (best-effort;
    /// requires a QuestDB build that supports <c>SET TTL</c>). 0 or negative = keep forever. Depth is
    /// the highest-volume stream, so the default trims it hardest.</summary>
    public int DepthRetentionDays { get; set; } = 14;
}
