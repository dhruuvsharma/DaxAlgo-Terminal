using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowToxicity;

public sealed class OrderFlowToxicityStrategy : ITradingStrategy
{
    public string Id => "order.flow.toxicity";
    public string DisplayName => "Order-flow toxicity / VPIN-style (L2)";
    public string Description => "VPIN-style |Î£ signed| / Î£|signed|. Mean-revert against high toxicity (Easley-LdP-O'Hara).";
}