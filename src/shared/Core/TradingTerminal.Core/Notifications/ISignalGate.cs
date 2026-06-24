namespace TradingTerminal.Core.Notifications;

/// <summary>
/// A veto applied to outbound notifications just before fan-out. The default implementation
/// lets everything through; the market-regime module replaces it with a gate that suppresses
/// <see cref="NotificationKind.Signal"/> alerts while the market is risk-off. Kept as its own
/// seam so the dispatcher stays decoupled from the regime feature — and so a test that wires
/// only the notification stack still resolves a no-op gate.
/// </summary>
public interface ISignalGate
{
    /// <summary>True if this notification should be dropped before reaching the transports.
    /// <paramref name="reason"/> is a short human-readable explanation for the log.</summary>
    bool ShouldSuppress(StrategyNotification notification, out string? reason);
}
