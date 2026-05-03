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
}
