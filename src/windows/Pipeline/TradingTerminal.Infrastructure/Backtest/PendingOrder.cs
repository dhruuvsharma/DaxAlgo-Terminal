using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>Mutable bookkeeping for an order resting in the simulated order book.</summary>
public sealed class PendingOrder
{
    public required OrderRequest Request { get; init; }
    public required string BrokerOrderId { get; init; }
    public long FilledQuantity { get; set; }
    public double TotalFillValue { get; set; }
    public OrderState State { get; set; } = OrderState.Working;

    public double? AveragePrice =>
        FilledQuantity == 0 ? null : TotalFillValue / FilledQuantity;
}
