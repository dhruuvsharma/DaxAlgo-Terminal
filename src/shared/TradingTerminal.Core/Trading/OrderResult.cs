namespace TradingTerminal.Core.Trading;

/// <summary>
/// Synchronous return value from <c>PlaceOrderAsync</c>. Reflects the order's state at
/// submission time only — subsequent transitions (fills, cancels) are pushed through
/// <c>OrderEvents</c>. <see cref="BrokerOrderId"/> is null until the broker assigns one.
/// </summary>
public sealed record OrderResult(
    string ClientOrderId,
    string? BrokerOrderId,
    OrderState State,
    string? RejectReason = null);
