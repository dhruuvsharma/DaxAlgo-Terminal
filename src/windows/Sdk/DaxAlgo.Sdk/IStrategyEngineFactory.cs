using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sdk;

/// <summary>
/// Stable, UI-free activation seam for a packaged strategy engine. A <c>.daxstrategy</c> manifest
/// names one public, parameterless implementation exactly; the host creates it only after bundle
/// integrity, trust, compatibility, and policy checks have completed.
/// </summary>
public interface IStrategyEngineFactory
{
    /// <summary>Declarative tunables used by live editors, backtests, and optimizers.</summary>
    StrategyParameterSchema Schema { get; }

    /// <summary>Market-data streams the engine requires.</summary>
    StrategyDataRequirement DataRequirement { get; }

    /// <summary>Creates a fresh strategy instance with a validated runtime parameter bag.</summary>
    IBacktestStrategy Create(Contract contract, StrategyParameters parameters);

    /// <summary>Creates a fresh strategy using the schema defaults.</summary>
    IBacktestStrategy Create(Contract contract) => Create(contract, Schema.CreateDefaults());
}
