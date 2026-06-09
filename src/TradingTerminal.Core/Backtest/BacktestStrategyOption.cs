using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Display + factory pair for a strategy that can be backtested. Held by the
/// backtest view-model's dropdown. Strategies are registered in
/// <c>BacktestStrategyCatalog</c> rather than via the live <c>IStrategyFactory</c>
/// — the live factory builds view-models, not engine-facing <c>IBacktestStrategy</c>
/// instances.
/// </summary>
public sealed record BacktestStrategyOption(
    string Id,
    string DisplayName,
    Func<Contract, IBacktestStrategy> Build,
    bool Fast = false)
{
    /// <summary>Declared tunables. <see cref="StrategyParameterSchema.Empty"/> when none.</summary>
    public StrategyParameterSchema Schema { get; init; } = StrategyParameterSchema.Empty;

    /// <summary>Factory that honours runtime parameters. When set, preferred over <see cref="Build"/>.</summary>
    public Func<Contract, StrategyParameters, IBacktestStrategy>? ParameterizedBuild { get; init; }

    /// <summary>True when this strategy advertises at least one tunable.</summary>
    public bool HasParameters => !Schema.IsEmpty;

    /// <summary>
    /// The market data this strategy consumes. Defaults to the universal baseline
    /// (<see cref="StrategyDataRequirement.L1"/> | <see cref="StrategyDataRequirement.Bars"/>);
    /// the catalog sets richer values per entry for strategies that need
    /// <see cref="StrategyDataRequirement.Depth"/> or <see cref="StrategyDataRequirement.TradeTape"/>.
    /// </summary>
    public StrategyDataRequirement DataRequirement { get; init; } =
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

    /// <summary>
    /// Builds a fresh strategy, applying <paramref name="parameters"/> when this option is
    /// parameterized. Falls back to schema defaults when none are supplied, and to the
    /// plain <see cref="Build"/> for strategies that declare no tunables.
    /// </summary>
    public IBacktestStrategy Create(Contract contract, StrategyParameters? parameters = null) =>
        ParameterizedBuild is { } build
            ? build(contract, parameters ?? Schema.CreateDefaults())
            : Build(contract);
}
