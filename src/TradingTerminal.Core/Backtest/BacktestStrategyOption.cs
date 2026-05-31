using TradingTerminal.Core.Domain;
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
    /// Builds a fresh strategy, applying <paramref name="parameters"/> when this option is
    /// parameterized. Falls back to schema defaults when none are supplied, and to the
    /// plain <see cref="Build"/> for strategies that declare no tunables.
    /// </summary>
    public IBacktestStrategy Create(Contract contract, StrategyParameters? parameters = null) =>
        ParameterizedBuild is { } build
            ? build(contract, parameters ?? Schema.CreateDefaults())
            : Build(contract);
}
