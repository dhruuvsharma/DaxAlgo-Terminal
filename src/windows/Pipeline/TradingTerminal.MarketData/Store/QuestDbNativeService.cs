using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Starts the bundled QuestDB Windows runtime as an app-owned child process, or attaches to an
/// externally managed loopback endpoint. Only an exact process handle created by this instance is
/// stopped during disposal; an already-running endpoint is never adopted or terminated.
/// </summary>
public sealed class QuestDbNativeService : IQuestDbLauncher, IDisposable
{
    private readonly MarketDataStoreOptions _options;
    private readonly IMarketDataStore _store;
    private readonly ILogger<QuestDbNativeService> _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private Process? _ownedProcess;
    private FileStream? _rootOwnership;
    private bool _disposed;

    public QuestDbNativeService(
        IOptions<MarketDataStoreOptions> options,
        IMarketDataStore store,
        ILogger<QuestDbNativeService> log)
    {
        _options = options.Value;
        _store = store;
        _log = log;
    }

    public bool IsApplicable => _options.Provider == MarketDataProvider.QuestDb;

    public bool AutoStart =>
        IsApplicable &&
        _options.QuestDbLaunchMode == QuestDbLaunchMode.Native &&
        _options.AutoStartQuestDb;

    public bool IsReachable() =>
        QuestDbNativeBootstrapper.IsReachable(_options);

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsApplicable)
            {
                _log.LogWarning(
                    "QuestDB isn't the configured market-data backend (Provider={Provider}); nothing to start.",
                    _options.Provider);
                return false;
            }

            if (!QuestDbNativeBootstrapper.HasSafeEndpoints(_options, out var endpointError))
            {
                _log.LogWarning(
                    "QuestDB startup was blocked because its endpoints are not safely configured: {Reason}",
                    endpointError);
                return false;
            }

            if (IsReachable())
            {
                _log.LogInformation("QuestDB is already reachable; using the existing endpoint.");
                return Reactivate();
            }

            if (_options.QuestDbLaunchMode == QuestDbLaunchMode.External)
            {
                _log.LogWarning(
                    "QuestDB is configured for External launch mode but the endpoint is unreachable. " +
                    "Start the external service and retry.");
                return false;
            }

            var timeout = QuestDbNativeBootstrapper.StartupTimeout(_options);
            ClearExitedOwnedProcess();
            var liveOwnedProcess = GetLiveOwnedProcess();
            if (liveOwnedProcess is not null)
            {
                _log.LogInformation(
                    "The app-owned QuestDB process is still running; rechecking its loopback endpoint.");
                var recovered = await QuestDbNativeBootstrapper.WaitUntilReachableAsync(
                    _options,
                    liveOwnedProcess,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                if (recovered) return Reactivate();

                if (!liveOwnedProcess.HasExited)
                {
                    _log.LogWarning(
                        "The app-owned QuestDB process is still running but its loopback endpoint is unavailable; " +
                        "it will not be replaced by another process.");
                    return false;
                }

                ClearExitedOwnedProcess();
            }

            QuestDbRuntimePaths paths;
            try
            {
                paths = QuestDbNativeBootstrapper.ResolvePaths(_options);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _log.LogWarning(ex, "QuestDB native paths are invalid; startup was blocked.");
                return false;
            }

            if (!QuestDbNativeBootstrapper.TryAcquireRootOwnership(paths, out var rootOwnership))
            {
                _log.LogInformation(
                    "Another desktop process owns the QuestDB native data root; waiting for its loopback endpoint.");
                if (await QuestDbNativeBootstrapper.WaitUntilReachableAsync(
                        _options,
                        timeout,
                        cancellationToken).ConfigureAwait(false))
                    return Reactivate();

                _log.LogWarning(
                    "The QuestDB data root is owned by another process, but its loopback endpoint is unavailable.");
                return false;
            }

            Process? process = null;
            try
            {
                process = QuestDbNativeBootstrapper.TryStart(paths, _log);
                if (process is null) return false;

                lock (_lifecycleGate)
                {
                    if (_disposed)
                    {
                        StopProcess(process);
                        throw new ObjectDisposedException(nameof(QuestDbNativeService));
                    }
                    _ownedProcess = process;
                    _rootOwnership = rootOwnership;
                    rootOwnership = null;
                }
            }
            finally
            {
                rootOwnership?.Dispose();
            }

            _log.LogInformation("Starting the bundled QuestDB runtime with data root {RootPath}.", paths.RootPath);
            bool reachable;
            try
            {
                reachable = await QuestDbNativeBootstrapper.WaitUntilReachableAsync(
                    _options,
                    process,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleaseOwnedProcess(process, stop: true);
                throw;
            }

            if (!reachable)
            {
                _log.LogWarning(
                    "QuestDB did not accept connections within {Seconds}s or exited during startup.",
                    (int)timeout.TotalSeconds);
                ReleaseOwnedProcess(process, stop: true);
                return false;
            }

            _log.LogInformation("QuestDB native runtime is ready.");
            return Reactivate();
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Dispose()
    {
        Process? process;
        FileStream? rootOwnership;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            process = _ownedProcess;
            _ownedProcess = null;
            rootOwnership = _rootOwnership;
            _rootOwnership = null;
        }

        try
        {
            if (process is not null)
                StopProcess(process);
        }
        finally
        {
            rootOwnership?.Dispose();
        }
    }

    private bool Reactivate()
    {
        if (_store is not IReactivatableTickStore reactivatable) return true;
        if (reactivatable.IsActive || reactivatable.TryActivate()) return true;

        _log.LogWarning(
            "QuestDB is reachable but the tick store could not be activated; restart the app to retry.");
        return false;
    }

    private Process? GetLiveOwnedProcess()
    {
        lock (_lifecycleGate)
        {
            if (_ownedProcess is null || _ownedProcess.HasExited) return null;
            return _ownedProcess;
        }
    }

    private void ClearExitedOwnedProcess()
    {
        Process? exited = null;
        FileStream? rootOwnership = null;
        lock (_lifecycleGate)
        {
            if (_ownedProcess is { HasExited: true })
            {
                exited = _ownedProcess;
                _ownedProcess = null;
                rootOwnership = _rootOwnership;
                _rootOwnership = null;
            }
        }
        exited?.Dispose();
        rootOwnership?.Dispose();
    }

    private void ReleaseOwnedProcess(Process process, bool stop)
    {
        var ownsProcess = false;
        FileStream? rootOwnership = null;
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_ownedProcess, process))
            {
                _ownedProcess = null;
                rootOwnership = _rootOwnership;
                _rootOwnership = null;
                ownsProcess = true;
            }
        }

        if (!ownsProcess) return;
        try
        {
            if (stop) StopProcess(process);
            else process.Dispose();
        }
        finally
        {
            rootOwnership?.Dispose();
        }
    }

    private void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                _log.LogInformation("Stopped the app-owned QuestDB runtime.");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not stop the app-owned QuestDB process cleanly.");
        }
        finally
        {
            process.Dispose();
        }
    }
}
