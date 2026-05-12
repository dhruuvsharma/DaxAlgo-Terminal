using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Transaction-cost analysis (TCA) — the standard post-trade report quant desks run to
/// answer "did execution cost us anything beyond gross PnL?". Inputs are the per-fill
/// records collected by the backtest engine; outputs are slippage, implementation
/// shortfall vs the period TWAP, maker/taker mix, and hourly fill quality.
///
/// Slippage convention: positive = the trader paid (filled WORSE than mid); negative =
/// the trader received price improvement. For a buy, slippage = fillPrice - midAtFill;
/// for a sell, slippage = midAtFill - fillPrice.
/// </summary>
public static class TransactionCostAnalysis
{
    public sealed record Report(
        int FillCount,
        long TotalQuantity,
        double TotalNotional,
        double TwapMid,
        double VwapFill,
        double ImplementationShortfall,           // VWAP - TWAP, signed (+ = cost vs benchmark)
        double MeanSlippage,                       // unweighted average per-fill slippage
        double VwapSlippage,                       // quantity-weighted slippage
        double SlippageP50,
        double SlippageP90,
        double SlippageP99,
        double MakerFraction,                      // [0, 1]
        double TakerFraction,
        IReadOnlyList<HourBucket> ByHourUtc);

    public sealed record HourBucket(int Hour, int Fills, double MeanSlippage, double MakerFraction);

    public static Report Compute(IReadOnlyList<FillRecord> fills)
    {
        if (fills.Count == 0)
            return new Report(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<HourBucket>());

        long totalQty = 0;
        double totalNotional = 0;
        double sumMid = 0;
        double sumSlippage = 0;
        double sumNotionalSlippage = 0;
        long maker = 0;
        var perFillSlippage = new double[fills.Count];

        for (var i = 0; i < fills.Count; i++)
        {
            var f = fills[i];
            var slip = f.Side == OrderSide.Buy ? f.Price - f.MidAtFill : f.MidAtFill - f.Price;
            perFillSlippage[i] = slip;

            totalQty += f.Quantity;
            totalNotional += f.Price * f.Quantity;
            sumMid += f.MidAtFill;
            sumSlippage += slip;
            sumNotionalSlippage += slip * f.Quantity;
            if (f.Liquidity == LiquidityFlag.Maker) maker += f.Quantity;
        }

        var twap = sumMid / fills.Count;
        var vwap = totalQty == 0 ? 0 : totalNotional / totalQty;
        var meanSlip = sumSlippage / fills.Count;
        var vwapSlip = totalQty == 0 ? 0 : sumNotionalSlippage / totalQty;
        var makerFraction = totalQty == 0 ? 0 : (double)maker / totalQty;

        var sorted = (double[])perFillSlippage.Clone();
        Array.Sort(sorted);
        double Percentile(double p) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];

        // Hour bucket aggregation — UTC hour-of-day.
        var byHour = fills
            .GroupBy(f => f.TimestampUtc.Hour)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                long h_qty = 0, h_maker = 0;
                double h_slipSum = 0;
                foreach (var f in g)
                {
                    var s = f.Side == OrderSide.Buy ? f.Price - f.MidAtFill : f.MidAtFill - f.Price;
                    h_slipSum += s;
                    h_qty += f.Quantity;
                    if (f.Liquidity == LiquidityFlag.Maker) h_maker += f.Quantity;
                }
                var count = g.Count();
                return new HourBucket(
                    Hour: g.Key,
                    Fills: count,
                    MeanSlippage: h_slipSum / count,
                    MakerFraction: h_qty == 0 ? 0 : (double)h_maker / h_qty);
            })
            .ToList();

        return new Report(
            FillCount: fills.Count,
            TotalQuantity: totalQty,
            TotalNotional: totalNotional,
            TwapMid: twap,
            VwapFill: vwap,
            ImplementationShortfall: vwap - twap,
            MeanSlippage: meanSlip,
            VwapSlippage: vwapSlip,
            SlippageP50: Percentile(0.50),
            SlippageP90: Percentile(0.90),
            SlippageP99: Percentile(0.99),
            MakerFraction: makerFraction,
            TakerFraction: 1.0 - makerFraction,
            ByHourUtc: byHour);
    }
}
