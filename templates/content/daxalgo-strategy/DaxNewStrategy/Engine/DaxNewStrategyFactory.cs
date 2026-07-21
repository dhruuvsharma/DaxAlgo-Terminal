using DaxAlgo.Sdk;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxNewStrategy.Engine;

/// <summary>
/// The manifest-named activation point shared by the live host and backtest worker. Keep this class
/// public, sealed, and parameterless; strategy logic belongs in <see cref="DaxNewStrategyKernel"/>.
/// </summary>
public sealed class DaxNewStrategyFactory : IStrategyEngineFactory
{
    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Int("fastPeriod", "Fast EMA", 20, min: 2, max: 200, group: "Signal"),
        StrategyParameter.Int("slowPeriod", "Slow EMA", 80, min: 3, max: 500, group: "Signal"),
        StrategyParameter.Int("quantity", "Quantity", 1, min: 1, max: 100, group: "Risk"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

    public IBacktestStrategy Create(Contract contract) => Create(contract, Schema.CreateDefaults());

    public IBacktestStrategy Create(Contract contract, StrategyParameters parameters) =>
        new DaxNewStrategyKernel(
            contract,
            parameters.GetInt("fastPeriod"),
            parameters.GetInt("slowPeriod"),
            parameters.GetLong("quantity"));
}
