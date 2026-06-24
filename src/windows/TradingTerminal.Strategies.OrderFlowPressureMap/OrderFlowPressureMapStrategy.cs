using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// <see cref="ITradingStrategy"/> metadata so the Order Flow Pressure Map appears in the shell's
/// Strategies pane (double-click opens its window via <see cref="IStrategyFactory"/>). It's a
/// multi-ticker monitor rather than a per-instrument signal generator, but it lives in the
/// Strategies list because that's where the user drives it: pick it, open the map, watch the
/// pressure. The data-requirement pills mirror what it consumes — L1 quotes + 1m bars, plus
/// optional L2 depth for a sharper book imbalance.
/// </summary>
public sealed class OrderFlowPressureMapStrategy : ITradingStrategy
{
    public string Id => "orderflow.pressuremap";

    public string DisplayName => "1-Minute Order Flow Pressure Map";

    public string Description =>
        "Single heatmap matrix (ticker × time) over the S&P 100/500 universe. Flags where unusual " +
        "1-minute volume is hitting and whether price is absorbing it (Absorption) or breaking through " +
        "(Breakthrough / Breakdown), with a per-cell intensity scaled by relative volume. Monitor only — " +
        "does not place orders.";

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth;

    /// <summary>Multi-ticker monitor over the S&amp;P 100/500 equity universe.</summary>
    public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Equity };

    public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
}
