namespace TradingTerminal.Core.Regime;

/// <summary>
/// The single source of truth for the current market-regime composite. The panel binds to
/// <see cref="Updates"/>, the signal gate reads <see cref="Current"/>, and the refresh loop
/// drives <see cref="RefreshAsync"/>. Implementations are connection-agnostic — the data is
/// pulled from public web endpoints, not the broker socket.
/// </summary>
public interface IMarketRegimeProvider
{
    /// <summary>The most recent snapshot, or <see cref="MarketRegimeSnapshot.Empty"/> before the
    /// first successful refresh. Never null so consumers don't have to null-check.</summary>
    MarketRegimeSnapshot Current { get; }

    /// <summary>Pushes a fresh snapshot after each successful (or degraded) recompute. Replays the
    /// latest value to new subscribers so a late-opening panel paints immediately.</summary>
    IObservable<MarketRegimeSnapshot> Updates { get; }

    /// <summary>Recompute now, ignoring the poll cadence. Returns the new snapshot. Folds all
    /// failures into a degraded snapshot rather than throwing.</summary>
    Task<MarketRegimeSnapshot> RefreshAsync(CancellationToken ct = default);
}
