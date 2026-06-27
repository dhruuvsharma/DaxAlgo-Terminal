using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Regime;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Background loop that recomputes the regime on a cadence and fires a notification when the
/// composite crosses into a new risk band. Runs an immediate refresh on startup so the panel
/// has data without waiting a full interval. The poll period is floored at five minutes to stay
/// polite to the free upstream endpoints.
/// </summary>
internal sealed class RegimeRefreshLoop : IHostedService
{
    private static readonly TimeSpan MinPeriod = TimeSpan.FromMinutes(5);

    private readonly IMarketRegimeProvider _provider;
    private readonly INotificationPublisher _notifications;
    private readonly IOptionsMonitor<MarketRegimeOptions> _options;
    private readonly ILogger<RegimeRefreshLoop> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private RegimeState? _lastNotifiedState;

    public RegimeRefreshLoop(
        IMarketRegimeProvider provider,
        INotificationPublisher notifications,
        IOptionsMonitor<MarketRegimeOptions> options,
        ILogger<RegimeRefreshLoop> logger)
    {
        _provider = provider;
        _notifications = notifications;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Market regime disabled — refresh loop not started.");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var snapshot = await _provider.RefreshAsync(ct).ConfigureAwait(false);
                MaybeNotify(snapshot);

                var minutes = Math.Max(MinPeriod.TotalMinutes, _options.CurrentValue.RefreshMinutes);
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market regime refresh loop crashed");
        }
    }

    private void MaybeNotify(MarketRegimeSnapshot snapshot)
    {
        if (snapshot.Unavailable || !_options.CurrentValue.NotifyOnRegimeChange) return;

        var state = snapshot.State;
        if (_lastNotifiedState is null) { _lastNotifiedState = state; return; }
        if (state == _lastNotifiedState) return;

        var from = _lastNotifiedState.Value;
        _lastNotifiedState = state;

        var direction = (int)state < (int)from ? "↓ risk-off" : "↑ risk-on";
        var msg = $"Market regime {from.Label()} → {state.Label()} "
                  + $"(composite {snapshot.CompositeScore:F0}). {direction}.";

        _ = _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.RegimeChange,
            StrategyId: "market.regime",
            StrategyName: "Market Regime",
            Symbol: "MARKET",
            Direction: direction,
            Message: msg,
            TimestampUtc: snapshot.GeneratedAtUtc));
    }
}
