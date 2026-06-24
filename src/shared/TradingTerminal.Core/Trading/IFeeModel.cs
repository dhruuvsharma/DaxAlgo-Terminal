namespace TradingTerminal.Core.Trading;

/// <summary>
/// Decides how much to charge for a single fill. Implementations encode broker / venue
/// schedules: per-share, per-contract, basis-point notional, or maker/taker splits.
///
/// Sign convention: positive = the trader pays; negative = the trader receives a rebate.
/// The router / ledger always subtracts the returned amount from cash, so a maker rebate
/// of <c>-0.0015 * qty</c> increases cash by the rebate.
/// </summary>
public interface IFeeModel
{
    /// <summary>
    /// Compute the fee (or rebate, if negative) for one fill. <paramref name="liquidity"/>
    /// indicates whether the fill took or made liquidity — strategies that post limits at
    /// the touch produce Maker fills; market orders and limits that cross the spread produce
    /// Taker fills.
    /// </summary>
    double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity);
}

/// <summary>Whether a fill took (crossed the spread) or made (rested) liquidity.</summary>
public enum LiquidityFlag
{
    Taker,
    Maker,
}

/// <summary>No fees, no rebates. The default — keeps existing backtests reproducible.</summary>
public sealed class ZeroFeeModel : IFeeModel
{
    public static readonly ZeroFeeModel Instance = new();
    public double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity) => 0;
}

/// <summary>
/// Two-sided schedule: pay <see cref="TakerFeePerUnit"/> when crossing, earn
/// <see cref="MakerRebatePerUnit"/> (positive number → rebate) when resting. Defaults match
/// no-fees if both are zero. Per-unit (share or contract); multiply outside if you want bps.
/// </summary>
public sealed class MakerTakerFeeModel : IFeeModel
{
    public double TakerFeePerUnit { get; }
    public double MakerRebatePerUnit { get; }

    public MakerTakerFeeModel(double takerFeePerUnit, double makerRebatePerUnit)
    {
        TakerFeePerUnit = takerFeePerUnit;
        MakerRebatePerUnit = makerRebatePerUnit;
    }

    public double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity) =>
        liquidity == LiquidityFlag.Maker
            ? -MakerRebatePerUnit * quantity
            : TakerFeePerUnit * quantity;
}

/// <summary>
/// Flat basis-point schedule on notional (price × quantity). Useful for equities where
/// commissions scale with $ volume. <c>bps = 1.0</c> ⇒ 1 basis point = 0.01% = 0.0001.
/// </summary>
public sealed class BpsFeeModel : IFeeModel
{
    public double Bps { get; }

    public BpsFeeModel(double bps) { Bps = bps; }

    public double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity) =>
        Math.Abs(price * quantity) * (Bps * 1e-4);
}
