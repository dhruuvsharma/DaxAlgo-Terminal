using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

/// <summary>
/// <see cref="ITradingStrategy"/> metadata for the Strategies pane. The engine-side
/// <c>IBacktestStrategy</c> implementation with the same name lives in
/// <c>TradingTerminal.Infrastructure.Backtest.Strategies</c> — different namespace, different
/// contract; do not unify them.
/// </summary>
public sealed class ImbalanceHeatFrontStrategy : ITradingStrategy
{
    public string Id => "imbalance.heatfront";
    public string DisplayName => "Imbalance Heat Front (L2 pressure surface)";
    public string Description => "3D bid/ask imbalance surface over [distance-from-touch × time]. Detects coherent ridges of one-sided book pressure and enters with (momentum) or against (mean-reversion) the ridge. Requires L2 depth — IB or cTrader.";
}
