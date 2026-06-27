using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrnsteinUhlenbeck;

public sealed class OrnsteinUhlenbeckStrategy : ITradingStrategy
{
    public string Id => "ornstein.uhlenbeck";
    public string DisplayName => "Ornstein-Uhlenbeck mean reversion";
    public string Description => "OLS-fit AR(1)-as-OU on the rolling price window; trade z-score deviations with separate entry / exit / stop bands.";

    /// <summary>
    /// OU mean-reversion consumes L1 bid/ask ticks for z-score estimation and
    /// bars aggregated downstream. No order-book depth or trade tape required.
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
}
