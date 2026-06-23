namespace TradingTerminal.Core.Trading;

/// <summary>
/// The strategy-facing seam for order submission. Live and backtest modes have separate
/// implementations: the live router delegates to the active <c>IBrokerClient</c> via
/// <c>IBrokerSelector</c>; the backtest router pushes orders into a simulated order book.
/// Strategies never reference <c>IBrokerClient</c> directly — they see this and only this.
/// </summary>
public interface IOrderRouter
{
    /// <summary>
    /// Submits an order. The returned <see cref="OrderResult"/> reflects state at submission;
    /// subsequent transitions stream through <see cref="OrderEvents"/>.
    /// </summary>
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);

    /// <summary>Cancels a working order by its client-assigned id. Idempotent.</summary>
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);

    /// <summary>Hot observable of every order lifecycle event from this router.</summary>
    IObservable<OrderEvent> OrderEvents { get; }
}
