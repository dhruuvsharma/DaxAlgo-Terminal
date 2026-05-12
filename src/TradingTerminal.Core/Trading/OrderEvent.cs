namespace TradingTerminal.Core.Trading;

/// <summary>
/// A single transition in the lifecycle of an order. <see cref="FilledQuantity"/> and
/// <see cref="AverageFillPrice"/> are cumulative across the order; <see cref="LastFillQuantity"/>
/// and <see cref="LastFillPrice"/> describe just this event (zero/null for non-fill events).
/// </summary>
public sealed record OrderEvent(
    DateTime TimestampUtc,
    string ClientOrderId,
    string? BrokerOrderId,
    OrderSide Side,
    OrderState State,
    long FilledQuantity,
    double? AverageFillPrice,
    long LastFillQuantity = 0,
    double? LastFillPrice = null,
    string? RejectReason = null,
    LiquidityFlag Liquidity = LiquidityFlag.Taker);
