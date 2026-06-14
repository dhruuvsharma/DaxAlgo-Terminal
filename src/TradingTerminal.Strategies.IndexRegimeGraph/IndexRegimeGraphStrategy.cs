using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

public sealed class IndexRegimeGraphStrategy : ITradingStrategy
{
    public string Id => "index.regime.graph";
    public string DisplayName => "Index Regime Graph";
    public string Description =>
        "Composite index-direction monitor. Runs the Advanced Market Regime indicator stack on every " +
        "constituent across all eight timeframes, blends each stock for a chosen horizon, multiplies by " +
        "index weight, and sums to a weighted up/down composite. Rendered as an interactive node graph " +
        "(Index ← stocks ← timeframes ← indicators) plus a quick-read side panel.";

    /// <summary>Bar-driven via the Advanced Regime provider (historical bars from the repository);
    /// no depth or trade tape required.</summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
}
