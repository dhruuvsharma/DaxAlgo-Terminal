using TradingTerminal.Core.Trading;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI;

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
    string? Note = null,
    StrategySignal? DirectSignal = null)
{
    public string SideText => DirectSignal?.Kind switch
    {
        StrategySignalKind.Long => "LONG",
        StrategySignalKind.Short => "SHORT",
        StrategySignalKind.Flat => "FLAT",
        _ => Side == OrderSide.Buy ? "BUY" : "SELL",
    };

    public string TypeText => DirectSignal is null ? OrderType.ToString() : "Signal";

    public string QuantityText => DirectSignal is { } signal
        ? signal.Strength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        : Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
}
