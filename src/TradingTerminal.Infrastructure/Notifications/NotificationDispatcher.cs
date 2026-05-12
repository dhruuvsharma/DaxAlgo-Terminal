using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications;

/// <summary>
/// The strategy-facing publisher. Writes to a bounded channel and drains it on a single
/// background loop, fanning each notification out to every transport that is currently
/// enabled. Strategy code never blocks on HTTP.
///
/// Drop policy: when the queue is full the oldest message is dropped (DropOldest) and a
/// warning is logged. We optimise for "the user always sees the latest fire".
/// </summary>
internal sealed class NotificationDispatcher : INotificationPublisher, IHostedService, IDisposable
{
    private readonly Channel<StrategyNotification> _channel;
    private readonly IEnumerable<INotificationTransport> _transports;
    private readonly IEnumerable<INotificationEnricher> _enrichers;
    private readonly ILogger<NotificationDispatcher> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NotificationDispatcher(
        IOptions<NotificationsOptions> options,
        IEnumerable<INotificationTransport> transports,
        IEnumerable<INotificationEnricher> enrichers,
        ILogger<NotificationDispatcher> logger)
    {
        var capacity = Math.Max(8, options.Value.QueueCapacity);
        _channel = Channel.CreateBounded<StrategyNotification>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _transports = transports;
        _enrichers = enrichers;
        _logger = logger;
    }

    public ValueTask PublishAsync(StrategyNotification notification, CancellationToken ct = default)
    {
        if (!_channel.Writer.TryWrite(notification))
            _logger.LogWarning("Notification dropped (queue full): {Kind} {Strategy}",
                notification.Kind, notification.StrategyId);
        return ValueTask.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _channel.Writer.TryComplete();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var n in _channel.Reader.ReadAllAsync(ct))
            {
                var enabled = _transports.Where(t => t.IsEnabled).ToArray();
                if (enabled.Length == 0) continue;

                // Run enrichers sequentially — order matters (each sees the previous one's
                // mutation) and the typical count is ≤ 1 so concurrency wouldn't help.
                var enriched = n;
                foreach (var enricher in _enrichers)
                {
                    if (!enricher.ShouldRun(enriched)) continue;
                    try { enriched = await enricher.EnrichAsync(enriched, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Enricher {Enricher} threw; using original", enricher.GetType().Name); }
                }

                var sends = enabled.Select(t => SendOne(t, enriched, ct));
                await Task.WhenAll(sends);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification dispatcher loop crashed");
        }
    }

    private async Task SendOne(INotificationTransport transport, StrategyNotification n, CancellationToken ct)
    {
        try
        {
            await transport.SendAsync(n, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transport {Transport} failed to send {Kind}", transport.Name, n.Kind);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
