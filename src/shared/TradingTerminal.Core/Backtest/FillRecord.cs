using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// One fill captured during a backtest. Used by transaction-cost analysis
/// (<see cref="TransactionCostAnalysis"/>) — we need the simultaneous mid to compute
/// slippage, and the liquidity flag to compute the maker/taker mix.
/// </summary>
public sealed record FillRecord(
    DateTime TimestampUtc,
    string ClientOrderId,
    OrderSide Side,
    long Quantity,
    double Price,
    double MidAtFill,
    LiquidityFlag Liquidity);
