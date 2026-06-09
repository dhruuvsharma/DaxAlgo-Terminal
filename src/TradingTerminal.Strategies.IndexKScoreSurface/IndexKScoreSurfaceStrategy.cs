using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IndexKScoreSurface;

public sealed class IndexKScoreSurfaceStrategy : ITradingStrategy
{
    public string Id => "index.kscore.surface";
    public string DisplayName => "Index K-Score Surface";
    public string Description =>
        "Multi-stock aggregator for index trading (US30 / S&P 500). " +
        "Per-component K ∈ [-1.5, +1.5] from 15 indicators × volatility-confidence multiplier; " +
        "threshold surface scales inversely with index weight; LONG/SHORT when enough components pierce with cumulative K conviction.";

    /// <summary>Tick-only K-score surface: consumes L1 quotes and bars (no depth or trade tape).</summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
}
