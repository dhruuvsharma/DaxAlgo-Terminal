namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Plug-in metadata for a strategy. The shell uses this for the Strategies list;
/// the actual view + view-model pair is constructed via <see cref="IStrategyFactory"/>.
/// </summary>
public interface ITradingStrategy
{
    /// <summary>Stable, unique identifier (e.g. "example.nvda.3m"). Used to dedupe tabs.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>
    /// The market data this strategy consumes. Defaults to the universal baseline
    /// (<see cref="StrategyDataRequirement.L1"/> | <see cref="StrategyDataRequirement.Bars"/>);
    /// only strategies that additionally need <see cref="StrategyDataRequirement.Depth"/> or
    /// <see cref="StrategyDataRequirement.TradeTape"/> override this. The wiring gates the
    /// extras against the connected broker's data-capability.
    /// </summary>
    StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
}
