using TradingTerminal.Core.Domain;

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
    bool Fast = false);
