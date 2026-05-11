using TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.App.Backtest;

/// <summary>
/// Registry of strategies the Backtest tab can run. Today it lists the demo
/// <c>BuyAndHoldStrategy</c>; Phase 7 ports the live strategies (RSI / Cumulative
/// Delta) into <see cref="Core.Backtest.IBacktestStrategy"/> adapters and adds them here.
/// </summary>
public static class BacktestStrategyCatalog
{
    public static IReadOnlyList<BacktestStrategyOption> All { get; } = new[]
    {
        new BacktestStrategyOption(
            Id: "buyAndHold",
            DisplayName: "Buy & hold (demo)",
            Build: contract => new BuyAndHoldStrategy(contract)),
        new BacktestStrategyOption(
            Id: "meanReversion",
            DisplayName: "Mean reversion (demo)",
            Build: contract => new MeanReversionStrategy(contract)),
    };
}
