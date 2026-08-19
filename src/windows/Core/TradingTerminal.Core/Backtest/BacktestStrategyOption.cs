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



    /// <summary>
    /// Optional factory a strategy may supply for a historical run, so it can ship a warmup/threshold
    /// preset without relaxing its conservative <em>live</em> defaults.
    ///
    /// <para><b>Inert, and deliberately kept.</b> The engine that consumed it was archived on
    /// 2026-08-17 and nothing in this tree reads it. It stays because this type is a <b>published
    /// contract</b>: plugins are compiled against the packaged <c>TradingTerminal.Core</c> and set this
    /// in their object initialisers, so deleting it is a binary-breaking change that fails an already
    /// installed plugin with <c>MissingMethodException</c> at load. Removing it belongs to a deliberate
    /// SDK version bump, not a cleanup — see issue #36.</para>
    /// </summary>
    public Func<Contract, IBacktestStrategy>? BacktestBuild { get; init; }

    /// <summary>Builds a strategy for a historical run, preferring <see cref="BacktestBuild"/> when the
    /// option ships a preset. Inert for the same reason as <see cref="BacktestBuild"/>.</summary>
    public IBacktestStrategy CreateForBacktest(Contract contract) =>
        BacktestBuild is { } build ? build(contract) : Create(contract);

    /// <summary>
    /// Optional factory producing this strategy's walk-forward parameter grid. Declared here so a
    /// plugin ships its grid with itself rather than the host hardcoding a per-strategy switch.
    ///
    /// <para>Inert and kept for the same ABI reason as <see cref="BacktestBuild"/>: the optimiser that
    /// swept it went to <c>archive/</c> with the engine, but plugins set this property.</para>
    /// </summary>
    public Func<WalkForwardAxes, IReadOnlyList<WalkForwardCandidate>>? WalkForwardGrid { get; init; }

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
    /// Optional canonical URL of the source research paper, mirroring
    /// <c>ITradingStrategy.ResearchPaperUrl</c>, so source provenance survives into the backtest
    /// catalog. Defaults to <c>null</c> for strategies without a paper source.
    /// </summary>
    public string? ResearchPaperUrl { get; init; }

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
