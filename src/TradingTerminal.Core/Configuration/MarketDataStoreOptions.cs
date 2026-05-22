namespace TradingTerminal.Core.Configuration;

/// <summary>Which backend persists the canonical market-data store.</summary>
public enum MarketDataProvider
{
    /// <summary>Embedded SQLite file — zero-config, always available.</summary>
    Sqlite = 0,

    /// <summary>PostgreSQL/TimescaleDB over the network (e.g. the docker-compose service). Falls back
    /// to SQLite automatically when the database can't be reached at startup.</summary>
    Postgres = 1,
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
}
