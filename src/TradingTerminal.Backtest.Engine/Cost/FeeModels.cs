using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Cost;

/// <summary>Maps the serializable <see cref="CostSpec"/> onto a concrete <see cref="IFeeModel"/>.
/// Reuses the Core fee models so live and backtest share one cost vocabulary.</summary>
internal static class FeeModels
{
    public static IFeeModel From(CostSpec cost) => cost.Model switch
    {
        CostModelKind.MakerTaker => new MakerTakerFeeModel(cost.TakerFeePerUnit, cost.MakerRebatePerUnit),
        CostModelKind.Bps => new BpsFeeModel(cost.FeeBps),
        _ => ZeroFeeModel.Instance,
    };
}
