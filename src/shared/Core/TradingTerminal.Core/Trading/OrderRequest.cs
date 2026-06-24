using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Trading;

/// <summary>
/// A request to submit a single order. <see cref="ClientOrderId"/> is a caller-generated
/// idempotency key — re-submitting with the same id MUST NOT produce a second order.
/// <see cref="LimitPrice"/> is required for <see cref="OrderType.Limit"/> and
/// <see cref="OrderType.StopLimit"/>; <see cref="StopPrice"/> is required for
/// <see cref="OrderType.Stop"/> and <see cref="OrderType.StopLimit"/>.
/// </summary>
public sealed record OrderRequest(
    string ClientOrderId,
    Contract Contract,
    OrderSide Side,
    OrderType Type,
    long Quantity,
    double? LimitPrice = null,
    double? StopPrice = null,
    TimeInForce TimeInForce = TimeInForce.Day);
