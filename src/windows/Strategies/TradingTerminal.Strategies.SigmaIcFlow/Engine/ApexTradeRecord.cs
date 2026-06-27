namespace TradingTerminal.Strategies.SigmaIcFlow.Engine;

/// <summary>
/// One completed <b>paper (simulated) trade</b> from the engine's internal OMS — entry → exit with
/// cost-inclusive realized P&amp;L. The engine fills these against the live mid/quote (no real broker
/// order is ever placed; this build is data/signals only), so the blotter shows what the strategy
/// <em>would</em> have done. Emitted in completion order via <c>ApexScalperStrategy.Trades</c>.
/// </summary>
/// <param name="EntryUtc">When the position was opened.</param>
/// <param name="ExitUtc">When it was closed.</param>
/// <param name="Direction">+1 long / −1 short.</param>
/// <param name="Quantity">Filled size (absolute).</param>
/// <param name="EntryPrice">Fill price at entry.</param>
/// <param name="ExitPrice">Fill price at exit.</param>
/// <param name="Pnl">Net realized P&amp;L in price-units·quantity, after spread + commission + slippage.</param>
/// <param name="ExitReason">Why it closed — "Target", "Stop", "Time", or "SessionEnd".</param>
public sealed record ApexTradeRecord(
    DateTime EntryUtc,
    DateTime ExitUtc,
    int Direction,
    long Quantity,
    double EntryPrice,
    double ExitPrice,
    double Pnl,
    string ExitReason);
