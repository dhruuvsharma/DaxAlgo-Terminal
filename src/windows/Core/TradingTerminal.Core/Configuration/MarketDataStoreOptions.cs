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

    /// <summary>Per-broker embedded SQLite: one time-series file per broker
    /// (<c>marketdata-{broker}.db</c>) for quotes/trades/bars, so concurrent brokers write in parallel
    /// (no single-writer lock contention), the same instrument's data never collides across brokers,
    /// and a broker's history can be wiped by deleting one file. Canonical instrument identity
    /// (the <c>instruments</c>/<c>instrument_aliases</c> registry) stays in the single shared
    /// <c>marketdata.db</c>, so <c>InstrumentId</c> remains broker-neutral and cross-venue tools
    /// keep working. This is the default. Switching from <see cref="Sqlite"/> starts the per-broker
    /// files fresh (existing single-file time-series is left untouched on disk, but unused).</summary>
    SqlitePerBroker = 3,
}

/// <summary>How the Windows app obtains the QuestDB process used by the split tick store.</summary>
public enum QuestDbLaunchMode
{
    /// <summary>Start the bundled native Windows runtime as an app-owned, per-user child process.</summary>
    Native = 0,

    /// <summary>Only connect to an endpoint managed outside the app; never start or stop a process.</summary>
    External = 1,
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

    /// <summary>Which storage backend to use. Defaults to <see cref="MarketDataProvider.SqlitePerBroker"/>
    /// (one time-series file per broker; shared identity registry). Postgres auto-falls back to SQLite.</summary>
    public MarketDataProvider Provider { get; set; } = MarketDataProvider.SqlitePerBroker;

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
    // QuestDB is a standalone time-series server. Writes use the InfluxDB Line Protocol over HTTP
    // (port 9000); reads use the
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

    // ── QuestDB native startup (Provider == QuestDb) ────────────────────────────────────────────
    // QuestDB is a standalone server with no embedded fallback for ticks. Native mode starts the
    // bundled Windows runtime without Docker; External mode only probes an endpoint managed elsewhere.

    /// <summary>Choose the bundled native runtime or an externally managed QuestDB endpoint.</summary>
    public QuestDbLaunchMode QuestDbLaunchMode { get; set; } = QuestDbLaunchMode.Native;

    /// <summary>When native mode is selected and QuestDB is unreachable, start the bundled runtime
    /// automatically. Set false to require the explicit File → Start QuestDB action.</summary>
    public bool AutoStartQuestDb { get; set; } = true;

    /// <summary>Optional path to <c>questdb.exe</c>. Empty resolves to
    /// <c>&lt;app-directory&gt;\questdb\bin\questdb.exe</c>; relative paths are app-directory relative.</summary>
    public string QuestDbExecutablePath { get; set; } = string.Empty;

    /// <summary>Optional writable QuestDB root. Empty resolves to
    /// <c>%LocalAppData%\DaxAlgoTerminal\QuestDB</c>; relative paths resolve beneath the same app-data folder.</summary>
    public string QuestDbRootPath { get; set; } = string.Empty;

    /// <summary>How long to wait for a newly started native runtime to accept PG-wire connections.</summary>
    public int QuestDbStartupTimeoutSeconds { get; set; } = 40;
}
