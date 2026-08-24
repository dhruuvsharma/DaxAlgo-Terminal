namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Which backend persists the canonical market-data store. Two, since 2026-08-23.
///
/// <para>There were four. Single-file <c>Sqlite</c> was a strictly worse <see cref="SqlitePerBroker"/>
/// — one writer for every stream, and it dropped depth — and <c>Postgres</c> was 511 lines of a
/// second store implementation that no shipped configuration selected. Both are gone; the numeric
/// values of the survivors are deliberately unchanged.</para>
/// </summary>
public enum MarketDataProvider
{
    /// <summary>
    /// QuestDB over ILP (writes) and the PostgreSQL wire protocol (reads) — every stream, bars
    /// included. <b>The default.</b>
    ///
    /// <para>There is <b>no silent fallback</b>: when QuestDB is unreachable, persistence is disabled
    /// and said so, rather than diverted to a store the user did not choose. Installed builds bundle
    /// the runtime and start it themselves.</para>
    /// </summary>
    QuestDb = 2,

    /// <summary>Per-broker embedded SQLite: one time-series file per broker
    /// (<c>marketdata-{broker}.db</c>) for quotes/trades/bars, so concurrent brokers write in parallel
    /// (no single-writer lock contention), the same instrument's data never collides across brokers,
    /// and a broker's history can be wiped by deleting one file. Canonical instrument identity
    /// (the <c>instruments</c>/<c>instrument_aliases</c> registry) stays in the single shared
    /// <c>marketdata.db</c>, so <c>InstrumentId</c> remains broker-neutral and cross-venue tools
    /// keep working. The zero-config fallback: no server, works offline, always available.</summary>
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
/// QuestDB (default; bundled runtime) and per-broker embedded SQLite (zero-config, no server).
/// </summary>
public sealed class MarketDataStoreOptions
{
    public const string SectionName = "MarketDataStore";

    /// <summary>Master switch for the persistence + ingest pipeline.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which storage backend to use. Defaults to <see cref="MarketDataProvider.SqlitePerBroker"/>
    /// (one time-series file per broker; shared identity registry). Postgres auto-falls back to SQLite.</summary>
    /// <summary>
    /// The backend. <see cref="MarketDataProvider.QuestDb"/> since 2026-08-22: the installer bundles
    /// a verified QuestDB runtime and the app starts it itself, so the high-volume streams get a
    /// time-series engine without the user installing anything.
    ///
    /// <para><b>Running from source has no bundled runtime.</b> It is staged at release-build time,
    /// so a <c>dotnet run</c> finds no <c>questdbin\questdb.exe</c>, QuestDB stays unreachable, and
    /// tick persistence is off while bars keep going to SQLite — loudly logged, never silently
    /// diverted. Run <c>scripts/stage-questdb.ps1</c> once to get the same behaviour as an install.</para>
    /// </summary>
    public MarketDataProvider Provider { get; set; } = MarketDataProvider.QuestDb;


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
    /// Retention windows per stream, in days. 0 or negative = keep forever.
    ///
    /// <para>These used to be enforced only by TimescaleDB's native retention policies — the doc
    /// here said outright that "SQLite ignores all three", and SQLite is the default backend, so the
    /// default install kept everything forever. <c>MarketDataRetentionService</c> now sweeps them on
    /// a timer through the store's own delete API, so they mean the same thing on every backend.</para>
    ///
    /// <para>Bars are tiny and the historical-cache value compounds, so they are kept indefinitely by
    /// default.</para>
    /// </summary>
    public int QuoteRetentionDays { get; set; } = 30;

    /// <summary>Trade-print retention in days. 0 or negative = keep forever.</summary>
    public int TradeRetentionDays { get; set; } = 30;

    /// <summary>
    /// OHLCV-bar retention in days. 0 or negative (default) = <b>keep forever</b>.
    ///
    /// <para>Bars deliberately do not run on the same clock as the tick streams. Retention is per
    /// stream, not per bar size, so one number has to serve both 1-minute bars (huge row counts, short
    /// useful lookback) and daily bars (a handful of rows, wanted for years). Any single figure ruins
    /// one end: 7 days makes daily history useless, and a year of 1-minute bars across a watchlist is
    /// hundreds of megabytes.</para>
    ///
    /// <para>Keeping them is also the cheap choice. Bars are two to three orders of magnitude smaller
    /// than depth, and they are the only stream whose deletion has a running cost: the cache in
    /// <c>MarketDataRepository</c> only hits when it holds at least the requested number of bars, so
    /// the retention window is a hard cap on the longest history request that can ever avoid a broker
    /// round trip — and Interactive Brokers rate-limits history hard.</para>
    /// </summary>
    public int BarRetentionDays { get; set; } = 0;

    /// <summary>
    /// Whether the retention sweep runs at all. Off means the windows above are advisory and the store
    /// grows without bound — which is what it did before the sweep existed.
    /// </summary>
    public bool RetentionSweepEnabled { get; set; } = true;

    /// <summary>
    /// How often the sweep runs, in hours. It also runs once shortly after startup.
    ///
    /// <para>Treated as a ceiling, not a promise: the sweep never runs less often than the shortest
    /// retention window, so shortening a window tightens the sweep automatically instead of leaving
    /// the two to disagree. Floored at fifteen minutes.</para>
    /// </summary>
    public int RetentionSweepIntervalHours { get; set; } = 1;

    /// <summary>
    /// Whether the sweep refuses to delete data the Telegram archive has not shipped yet.
    ///
    /// <para>On by default, and it matters: a retention window shorter than the archive period would
    /// delete each period before the archiver ever bundled it, destroying data the user believed was
    /// being backed up.</para>
    /// </summary>
    public bool RespectPendingArchives { get; set; } = true;

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

    /// <summary>
    /// Depth (L2) retention in <b>hours</b>, not days. 0 or negative = keep forever.
    ///
    /// <para>Hours because depth is not on the same time-scale as anything else here. It is stored as
    /// one row per book level per snapshot — ten levels a side at ten snapshots a second is 200 rows
    /// per second for a single instrument — so it outgrows every other stream by orders of magnitude.
    /// And the only thing that reads it back is the order book's warm start, which replays <b>thirty
    /// minutes</b>. A window measured in days was keeping hundreds of times what anything asked for.</para>
    ///
    /// <para>The old <c>DepthRetentionDays</c> key is inert; a config still setting it is simply
    /// ignored and this default applies.</para>
    /// </summary>
    public int DepthRetentionHours { get; set; } = 1;

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
