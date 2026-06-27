using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowToxicity;

public sealed class OrderFlowToxicityStrategy : ITradingStrategy
{
    public string Id => "order.flow.toxicity";

    /// <summary>
    /// Display name. The previous label said "(L2)" but the engine-side
    /// <c>OrderFlowToxicityStrategy</c> (Infrastructure) overrides only
    /// <c>OnTickAsync</c> — no <c>OnDepthAsync</c>, no <c>OnTradeAsync</c>.
    /// Toxicity is approximated from L1 bid/ask mid-moves (tick-rule proxy),
    /// not from real L2 depth or the trade tape. Corrected here.
    /// </summary>
    public string DisplayName => "Order-flow toxicity / VPIN-style (L1 approx.)";

    public string Description => "VPIN-style |Σ signed| / Σ|signed|. Mean-revert against high toxicity (Easley-LdP-O'Hara).";

    /// <summary>
    /// Universal baseline only (<see cref="StrategyDataRequirement.L1"/> |
    /// <see cref="StrategyDataRequirement.Bars"/>).
    /// Despite the "order-flow toxicity / VPIN" name, the engine implementation
    /// uses only L1 quotes: it classifies volume buckets by tick-rule on the
    /// bid/ask mid-price — no Level-2 depth (<c>OnDepthAsync</c> is not
    /// overridden) and no trade-tape (<c>OnTradeAsync</c> is not overridden).
    /// A true VPIN would require <see cref="StrategyDataRequirement.TradeTape"/>;
    /// that is a future engine upgrade, not declared here.
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
}