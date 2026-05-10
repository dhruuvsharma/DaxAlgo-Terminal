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
    private readonly ILogger<NotificationDispatcher> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NotificationDispatcher(
        IOptions<NotificationsOptions> options,
        IEnumerable<INotificationTransport> transports,
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

                var sends = enabled.Select(t => SendOne(t, n, ct));
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
