namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Resolved at DI registration so the UI can tell whether the live TWS client is wired
/// or the synthetic fallback is in use. Lets login / status indicators communicate the
/// mode honestly without leaking implementation types into the rest of the app.
/// </summary>
/// <param name="IsLive">True when the real TWS client is active. False when running on the synthetic <c>FakeIbClient</c>.</param>
/// <param name="DisplayName">Short human-readable label (e.g. "Live TWS" or "Demo (synthetic data)").</param>
/// <param name="Description">Longer explanation suitable for tooltip / sub-label text.</param>
public sealed record IbConnectionMode(bool IsLive, string DisplayName, string Description);
