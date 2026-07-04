using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using TradingTerminal.Infrastructure.MarketData.Store;

namespace TradingTerminal.Infrastructure.MarketData;

public static class MarketDataPipelineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the canonical market-data pipeline: the live in-memory hub, the instrument
    /// registry and store, and the ingest service. Backend selection (SQLite vs
    /// PostgreSQL/TimescaleDB) is resolved once at startup; if Postgres is configured but
    /// unreachable (Docker not running), it transparently falls back to SQLite so the app always
    /// starts. The chosen store and registry share one backend for the app's lifetime.
    /// </summary>
    public static IServiceCollection AddMarketDataPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(MarketDataStoreOptions.SectionName);
        services.Configure<MarketDataStoreOptions>(section);

        var opts = section.Get<MarketDataStoreOptions>() ?? new MarketDataStoreOptions();
        var dbPath = ResolveDatabasePath(opts.DatabasePath);          // .../marketdata.db — the shared registry file
        var sqliteConn = BuildSqliteConnectionString(dbPath);
        var pgConn = opts.PostgresConnectionString;

        // Decide the backend now: Postgres only if configured AND reachable, else SQLite.
        var usePostgres = opts.Provider == MarketDataProvider.Postgres && CanReachPostgres(pgConn);

        services.AddSingleton<IMarketDataHub, MarketDataHub>();

        // Trained-model registry: its own small SQLite file (default ml-models.db), independent of the
        // market-data backend. Registered as a lazy singleton factory — nothing resolves it until a
        // chart/strategy actually checkpoints or loads a model, so users who never touch ML pay no cost
        // (the DB file isn't created). Holds only model weights + metadata, never market data.
        var modelSection = configuration.GetSection(ModelRegistryOptions.SectionName);
        services.Configure<ModelRegistryOptions>(modelSection);
        var modelOpts = modelSection.Get<ModelRegistryOptions>() ?? new ModelRegistryOptions();
        services.AddSingleton<IModelRegistry>(_ =>
        {
            var path = string.IsNullOrWhiteSpace(modelOpts.DatabasePath)
                ? ResolveModelDatabasePath()
                : modelOpts.DatabasePath;
            var registry = new SqliteModelRegistry(path);
            if (modelOpts.RetentionDays > 0) registry.PruneOlderThan(modelOpts.RetentionDays);
            return registry;
        });

        // The canonical identity registry stays single-file (the shared marketdata.db) regardless of
        // backend — including the per-broker store, whose split is only of the time-series tables.
        // One registry assigns globally-consistent InstrumentIds that every per-broker file references.
        services.AddSingleton<IInstrumentRegistry>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>();
            IInstrumentPersistence persistence = usePostgres
                ? new NpgsqlInstrumentPersistence(pgConn, log.CreateLogger<NpgsqlInstrumentPersistence>())
                : new SqliteInstrumentPersistence(sqliteConn);
            return new InstrumentRegistry(persistence, log.CreateLogger<InstrumentRegistry>());
        });

        services.AddSingleton<IMarketDataStore>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();

            if (opts.Provider == MarketDataProvider.QuestDb)
                return BuildQuestDbStore(sqliteConn, opts, lf);

            if (opts.Provider == MarketDataProvider.SqlitePerBroker)
                return new PerBrokerSqliteMarketDataStore(
                    Path.GetDirectoryName(dbPath)!, Path.GetFileNameWithoutExtension(dbPath),
                    opts.PersistLiveData, opts.WriteBatchSize, opts.DepthRetentionDays, lf);

            if (usePostgres)
            {
                lf.CreateLogger("MarketData").LogInformation("Market-data store: PostgreSQL/TimescaleDB.");
                return new NpgsqlMarketDataStore(
                    pgConn, opts.PersistLiveData, opts.WriteBatchSize,
                    opts.QuoteRetentionDays, opts.TradeRetentionDays, opts.BarRetentionDays,
                    lf.CreateLogger<NpgsqlMarketDataStore>());
            }

            if (opts.Provider == MarketDataProvider.Postgres)
                lf.CreateLogger("MarketData").LogWarning("Postgres unreachable — falling back to embedded SQLite store.");
            else
                lf.CreateLogger("MarketData").LogInformation("Market-data store: embedded SQLite.");

            return new SqliteMarketDataStore(sqliteConn, opts.PersistLiveData, opts.WriteBatchSize,
                lf.CreateLogger<SqliteMarketDataStore>());
        });

        services.AddSingleton<IMarketDataIngest, MarketDataIngestService>();

        // Backs the manual File → Start QuestDB command (launch Docker Desktop + container, re-arm live).
        // Exposed via IQuestDbLauncher too, so store-agnostic layers (the login screen) can warm it up.
        services.AddSingleton<QuestDbDockerService>();
        services.AddSingleton<IQuestDbLauncher>(sp => sp.GetRequiredService<QuestDbDockerService>());

        // Instrument-universe pre-loader. Hosted service so it kicks in once the host starts and
        // reacts to every Connected transition on every registered broker — the service subscribes
        // to each per-broker state stream via IBrokerSelector.StateOf, so multi-broker setups run
        // one discovery pass per broker independently. Registered as a singleton (for direct
        // resolution if needed) and as an IHostedService (for the host to start). Plain
        // AddSingleton<IHostedService> here rather than TryAddEnumerable because factory-based
        // descriptors don't carry an implementation type, which the latter requires for its dedup
        // check.
        services.AddSingleton<InstrumentDiscoveryService>(sp => new InstrumentDiscoveryService(
            sp.GetRequiredService<IBrokerSelector>(),
            sp.GetRequiredService<IInstrumentRegistry>(),
            sp.GetRequiredService<ILogger<InstrumentDiscoveryService>>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InstrumentDiscoveryService>());

        return services;
    }

    /// <summary>
    /// Builds the split QuestDB store: quotes/trades/depth → QuestDB, bars → SQLite, wrapped in a
    /// <see cref="CompositeMarketDataStore"/>. Unlike the Postgres path there is <b>no silent
    /// fallback</b> — if QuestDB is unreachable we log loudly and the QuestDB half goes inert
    /// (tick/depth persistence off) while bars keep flowing to SQLite, so the app still launches.
    /// </summary>
    private static IMarketDataStore BuildQuestDbStore(string sqliteConn, MarketDataStoreOptions opts, ILoggerFactory lf)
    {
        var log = lf.CreateLogger("MarketData");

        var barStore = new SqliteMarketDataStore(
            sqliteConn, opts.PersistLiveData, opts.WriteBatchSize, lf.CreateLogger<SqliteMarketDataStore>());

        // Probe only — never block here. The store is resolved on the UI thread (the login screen
        // pulls in the launcher), so the actual Docker start runs asynchronously from the login screen
        // (QuestDbDockerService, honouring AutoStartDocker) or File → Start QuestDB, then re-arms the
        // store live via IReactivatableTickStore. If QuestDB is already up we wire the sender now.
        var reachable = CanReachQuestDb(opts.QuestDbPgConnectionString);
        if (!reachable)
            log.LogWarning(
                "QuestDB not reachable yet ({Conn}) — L1/L2/trade persistence stays off until it's up. " +
                "It will be started from the login screen (or File → Start QuestDB) and engaged without a restart. " +
                "Bars still persist to SQLite.",
                opts.QuestDbPgConnectionString);

        var tickStore = new QuestDbMarketDataStore(
            opts.QuestDbIlpConfig, opts.QuestDbPgConnectionString,
            opts.PersistLiveData, reachable, opts.WriteBatchSize, opts.DepthRetentionDays,
            lf.CreateLogger<QuestDbMarketDataStore>());

        return new CompositeMarketDataStore(tickStore, barStore, lf.CreateLogger<CompositeMarketDataStore>());
    }

    /// <summary>Best-effort startup probe — opens and closes a connection to decide availability.</summary>
    private static bool CanReachPostgres(string connectionString) => CanReachQuestDb(connectionString);

    /// <summary>Probe a PostgreSQL-wire endpoint (Postgres/TimescaleDB or QuestDB) for reachability.</summary>
    private static bool CanReachQuestDb(string connectionString)
    {
        try
        {
            using var cn = new NpgsqlConnection(connectionString);
            cn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveDatabasePath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "marketdata.db");
    }

    private static string ResolveModelDatabasePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ml-models.db");
    }

    private static string BuildSqliteConnectionString(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }
}
