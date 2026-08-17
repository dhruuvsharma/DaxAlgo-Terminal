using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>A live order resting in the simulated book, tagged with the instrument it trades so a
/// portfolio run evaluates each order only against its own instrument's quotes.</summary>
internal sealed class WorkingOrder
{
    public required OrderRequest Request { get; init; }
    public required InstrumentId Instrument { get; init; }
    public required string BrokerOrderId { get; init; }

    public long FilledQuantity { get; set; }
    public double TotalFillValue { get; set; }
    public OrderState State { get; set; } = OrderState.Working;

    public double? AveragePrice => FilledQuantity == 0 ? null : TotalFillValue / FilledQuantity;
}
