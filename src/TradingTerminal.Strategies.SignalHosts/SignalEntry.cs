using TradingTerminal.Core.Trading;

namespace TradingTerminal.Strategies.SignalHosts;

/// <summary>
/// One signal row in the live signal log. Produced every time the wrapped
/// <c>IBacktestStrategy</c> calls <c>PlaceOrderAsync</c> on its router. The display grid
/// binds to a list of these.
/// </summary>
public sealed record SignalEntry(
    DateTime TimestampUtc,
    OrderSide Side,
    long Quantity,
    OrderType OrderType,
    double Price,
    double Mid,
    string? Note = null)
{
    public string SideText => Side == OrderSide.Buy ? "BUY" : "SELL";
    public string TypeText => OrderType.ToString();
    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
}
