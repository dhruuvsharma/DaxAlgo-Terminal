namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Resolved at DI registration so the UI can tell whether a real broker client is wired
/// or the synthetic fallback is in use. Lets login / status indicators communicate the
/// mode honestly without leaking implementation types into the rest of the app.
/// </summary>
/// <param name="Broker">Which broker this mode describes.</param>
/// <param name="IsLive">True when the real client is active. False when the synthetic fallback is in use.</param>
/// <param name="DisplayName">Short human-readable label (e.g. "Live TWS" or "Demo (synthetic data)").</param>
/// <param name="Description">Longer explanation suitable for tooltip / sub-label text.</param>
public sealed record BrokerConnectionMode(
    BrokerKind Broker,
    bool IsLive,
    string DisplayName,
    string Description);
