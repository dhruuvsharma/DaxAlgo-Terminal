using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Updates;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// Runs the update check shortly after start-up and then on an interval, raising
/// <see cref="UpdateAvailable"/> when a newer version is published.
///
/// The start-up check is deliberately delayed and always off the UI thread: a release host that is
/// slow or down must never delay the window appearing. Every failure is swallowed and logged.
/// </summary>
public sealed class UpdateCheckService : IHostedService, IUpdateNotifier, IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly IUpdateChecker _checker;
    private readonly UpdatesOptions _options;
    private readonly ILogger<UpdateCheckService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UpdateCheckService(IUpdateChecker checker, UpdatesOptions options, ILogger<UpdateCheckService> logger)
    {
        _checker = checker;
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised on a background thread when a newer version is published. Marshal to the UI yourself.</summary>
    public event Action<UpdateCheckResult>? UpdateAvailable;

    /// <summary>The most recent result, so a late subscriber (a window opened after the first check) can catch up.</summary>
    public UpdateCheckResult? Latest { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (_options.CheckOnStartup)
            {
                await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
                await CheckOnceAsync(ct).ConfigureAwait(false);
            }

            // Clamped so a misconfigured interval cannot hammer the release host.
            var hours = _options.CheckIntervalHours < 1 ? 1 : _options.CheckIntervalHours;
            using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await CheckOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var result = await _checker.CheckAsync(ct).ConfigureAwait(false);
            Latest = result;

            switch (result.Outcome)
            {
                case UpdateOutcome.UpdateAvailable:
                    _logger.LogInformation(
                        "Update available: {Available} (running {Current}){Cached}.",
                        result.Available!.Version, result.Current, result.FromCache ? " [cached feed]" : string.Empty);
                    UpdateAvailable?.Invoke(result);
                    break;
                case UpdateOutcome.Failed:
                    _logger.LogDebug("Update check failed: {Detail}", result.Detail);
                    break;
                case UpdateOutcome.UpToDate:
                    _logger.LogDebug("Update check: {Current} is current.", result.Current);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A checker is contracted never to throw; if one does, it still must not take the app down.
            _logger.LogWarning(ex, "Update check threw unexpectedly and was ignored.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => _cts?.Dispose();
}
