using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Backs the manual <c>File → Start QuestDB</c> command and the login-screen auto-start. Unlike the
/// lightweight store-factory path, this will start the Docker engine if the daemon is down — headlessly
/// via the CLI (<c>docker desktop start</c>), falling back to launching the Docker Desktop app — wait
/// for it, start the QuestDB container, then re-arm the store live (no app restart) so tick/depth
/// persistence engages immediately. Every step
/// reports to the universal activity log. Safe to call when QuestDB isn't the configured backend — it
/// just says so.
/// </summary>
public sealed class QuestDbDockerService : IQuestDbLauncher
{
    private readonly MarketDataStoreOptions _opts;
    private readonly IMarketDataStore _store;
    private readonly ILogger<QuestDbDockerService> _log;

    public QuestDbDockerService(
        IOptions<MarketDataStoreOptions> opts, IMarketDataStore store, ILogger<QuestDbDockerService> log)
    {
        _opts = opts.Value;
        _store = store;
        _log = log;
    }

    public bool IsQuestDbBackend => _opts.Provider == MarketDataProvider.QuestDb;

    // ── IQuestDbLauncher ─────────────────────────────────────────────────────────────────────────
    public bool IsApplicable => IsQuestDbBackend;
    public bool AutoStart => _opts.AutoStartDocker;
    public bool IsReachable() => QuestDbDockerBootstrapper.IsReachable(_opts.QuestDbPgConnectionString);

    /// <summary>Runs the full start sequence off the calling (UI) thread. Returns true when QuestDB ends
    /// up reachable and the store is live.</summary>
    public Task<bool> StartAsync(CancellationToken ct = default) => Task.Run(() => StartCore(ct), ct);

    private bool StartCore(CancellationToken ct)
    {
        if (!IsQuestDbBackend)
        {
            _log.LogWarning(
                "QuestDB isn't the configured market-data backend (Provider={Provider}); nothing to start.",
                _opts.Provider);
            return false;
        }

        if (QuestDbDockerBootstrapper.IsReachable(_opts.QuestDbPgConnectionString))
        {
            _log.LogInformation("QuestDB is already running.");
            return Reactivate();
        }

        if (!QuestDbDockerBootstrapper.DockerCliPresent())
        {
            _log.LogWarning("Docker CLI not found — install Docker Desktop, then retry File → Start QuestDB.");
            return false;
        }

        if (!QuestDbDockerBootstrapper.DockerDaemonReady())
        {
            _log.LogInformation("Docker daemon isn't responding — starting the Docker engine…");

            // Prefer a headless CLI engine start (`docker desktop start`) so no Docker Desktop window
            // pops up. Only fall back to launching the GUI app when the CLI plugin is unavailable.
            var ready = QuestDbDockerBootstrapper.TryStartDockerEngineCli(_log)
                        && QuestDbDockerBootstrapper.WaitForDaemon(TimeSpan.FromSeconds(120), ct);

            if (!ready)
            {
                _log.LogInformation("Headless start unavailable — launching Docker Desktop…");
                if (!QuestDbDockerBootstrapper.TryLaunchDockerDesktop(_opts, _log))
                {
                    _log.LogWarning(
                        "Couldn't start Docker automatically. Start Docker Desktop manually (or set " +
                        "MarketDataStore:DockerDesktopPath), then retry File → Start QuestDB.");
                    return false;
                }
                if (!QuestDbDockerBootstrapper.WaitForDaemon(TimeSpan.FromSeconds(120), ct))
                {
                    _log.LogWarning(
                        "Docker didn't become ready in time. Once its whale icon is steady, retry File → Start QuestDB.");
                    return false;
                }
            }
            _log.LogInformation("Docker daemon is ready.");
        }

        _log.LogInformation("Starting the QuestDB container…");
        if (!QuestDbDockerBootstrapper.TryStartContainer(_opts, _log))
        {
            _log.LogWarning("Could not start the QuestDB container — see the entries above.");
            return false;
        }

        var timeout = QuestDbDockerBootstrapper.StartupTimeout(_opts);
        if (!QuestDbDockerBootstrapper.WaitUntilReachable(_opts.QuestDbPgConnectionString, timeout, ct))
        {
            _log.LogWarning(
                "Container started but QuestDB didn't accept connections within {Seconds}s.", (int)timeout.TotalSeconds);
            return false;
        }

        _log.LogInformation("QuestDB is up.");
        return Reactivate();
    }

    /// <summary>Flip the (possibly inert) store live so persistence engages without a restart.</summary>
    private bool Reactivate()
    {
        if (_store is IReactivatableTickStore r)
        {
            if (r.IsActive) return true;
            if (r.TryActivate()) return true;
            _log.LogWarning(
                "QuestDB is reachable but the store couldn't be activated — restart the app to enable tick persistence.");
            return false;
        }
        return true;
    }
}
