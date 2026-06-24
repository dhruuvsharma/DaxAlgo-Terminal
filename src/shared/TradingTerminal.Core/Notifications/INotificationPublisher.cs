namespace TradingTerminal.Core.Notifications;

/// <summary>
/// What strategies call to surface a signal/trade. Implementations buffer and dispatch
/// asynchronously — the call must never block the strategy thread.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Hand off a notification for delivery. Returns immediately once the message is
    /// queued. Drops with a logged warning if the queue is full.
    /// </summary>
    ValueTask PublishAsync(StrategyNotification notification, CancellationToken ct = default);
}
