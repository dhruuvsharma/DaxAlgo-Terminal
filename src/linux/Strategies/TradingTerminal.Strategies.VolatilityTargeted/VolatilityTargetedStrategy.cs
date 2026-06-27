using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.VolatilityTargeted;

public sealed class VolatilityTargetedStrategy : ITradingStrategy
{
    public string Id => "vol.targeted";
    public string DisplayName => "Volatility targeting (index)";
    public string Description => "Position = target_vol / realised_vol_ewma. AQR-style risk-parity overlay.";

    /// <summary>Designed as an index risk-parity overlay; single-instrument.</summary>
    public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Index };

    /// <summary>
    /// Consumes L1 top-of-book quotes only (mid-price returns drive the EWMA variance
    /// estimate). Bars are aggregated downstream from the same L1 stream — the universal
    /// baseline. No depth or trade-tape required.
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
}
