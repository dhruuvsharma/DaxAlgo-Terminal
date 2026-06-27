namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Resolves a registered strategy into a (view, view-model) pair. The shell never
/// constructs strategy types directly — adding a new strategy is one DI registration.
/// </summary>
public interface IStrategyFactory
{
    IReadOnlyList<ITradingStrategy> All { get; }

    /// <summary>
    /// Builds a fresh host for the given strategy id.
    /// Throws <see cref="KeyNotFoundException"/> if the id is not registered.
    /// </summary>
    StrategyHost Create(string strategyId);
}
