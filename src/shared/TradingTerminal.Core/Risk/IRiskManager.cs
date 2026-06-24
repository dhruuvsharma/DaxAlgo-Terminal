using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Risk;

/// <summary>
/// Pre-trade risk check. Sits between the strategy and the broker / simulated order book.
/// Implementations track net position per symbol and realised PnL per UTC trading day,
/// rejecting submissions that would breach configured caps. The order router observes
/// every fill through <see cref="RecordFill"/> so position and PnL stay in sync — strategies
/// never touch this directly.
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Decides whether <paramref name="request"/> is allowed at this moment.
    /// Returns <c>(true, null)</c> when accepted; <c>(false, reason)</c> when rejected.
    /// </summary>
    (bool Allowed, string? RejectReason) Evaluate(OrderRequest request);

    /// <summary>
    /// Updates internal position / PnL state from a fill. <paramref name="symbol"/> is the
    /// instrument's <c>Contract.Symbol</c>; the order router has it from the original request
    /// and forwards it so the manager doesn't need to thread the full Contract through every
    /// event. Idempotent per (ClientOrderId, FilledQuantity) pair.
    /// </summary>
    void RecordFill(string symbol, OrderEvent fillEvent);
}
