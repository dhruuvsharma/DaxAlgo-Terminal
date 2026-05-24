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
        var sqliteConn = BuildSqliteConnectionString(ResolveDatabasePath(opts.DatabasePath));
        var pgConn = opts.PostgresConnectionString;

        // Decide the backend now: Postgres only if configured AND reachable, else SQLite.
        var usePostgres = opts.Provider == MarketDataProvider.Postgres && CanReachPostgres(pgConn);

        services.AddSingleton<IMarketDataHub, MarketDataHub>();

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

        // Instrument-universe pre-loader. Hosted service so it kicks in once the host starts and
        // reacts to every Connected transition; bound to the ConnectionManager's reactive state
        // stream (which itself re-wires on broker switch, so a switch re-fires discovery for the
        // new broker). Registered as a singleton (for direct resolution if needed) and as an
        // IHostedService (for the host to start). Plain AddSingleton<IHostedService> here rather
        // than TryAddEnumerable because factory-based descriptors don't carry an implementation
        // type, which the latter requires for its dedup check.
        services.AddSingleton<InstrumentDiscoveryService>(sp => new InstrumentDiscoveryService(
            sp.GetRequiredService<IBrokerSelector>(),
            sp.GetRequiredService<Ib.ConnectionManager>().ConnectionState,
            sp.GetRequiredService<IInstrumentRegistry>(),
            sp.GetRequiredService<ILogger<InstrumentDiscoveryService>>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InstrumentDiscoveryService>());

        return services;
    }

    /// <summary>Best-effort startup probe — opens and closes a connection to decide availability.</summary>
    private static bool CanReachPostgres(string connectionString)
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

    private static string BuildSqliteConnectionString(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }
}
