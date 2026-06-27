using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// A round-trip trade: an entry fill and the matching exit fill that flattens the position.
/// <see cref="GrossPnl"/> is in price points; multiply by the contract multiplier to get dollars.
/// </summary>
public sealed record Trade(
    DateTime EntryUtc,
    DateTime ExitUtc,
    OrderSide Side,
    long Quantity,
    double EntryPrice,
    double ExitPrice,
    double GrossPnl);
