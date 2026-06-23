namespace TradingTerminal.Core.Notifications;

/// <summary>
/// Pipeline step that mutates a <see cref="StrategyNotification"/> before the dispatcher
/// hands it to transports. Multiple enrichers are applied in registration order; each
/// returns a possibly-modified notification (or the same instance for no-op).
///
/// Typical use: append context from an external service — e.g. an LLM commentary tag
/// from a local Ollama instance, a sentiment score from a news API, a regime-detection
/// label from a hosted model.
/// </summary>
public interface INotificationEnricher
{
    /// <summary>Whether this enricher should run for this notification.</summary>
    bool ShouldRun(StrategyNotification notification);

    /// <summary>Return the enriched notification. Implementations MUST NOT throw —
    /// errors should be logged and the original returned unchanged.</summary>
    Task<StrategyNotification> EnrichAsync(StrategyNotification notification, CancellationToken ct);
}
