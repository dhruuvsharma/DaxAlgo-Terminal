using TradingTerminal.Core.Trading;
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
        new BacktestStrategyOption(
            Id: "donchianBreakout",
            DisplayName: "Donchian breakout (demo)",
            Build: contract => new DonchianBreakoutStrategy(contract)),
        new BacktestStrategyOption(
            Id: "microprice",
            DisplayName: "Microprice deviation (microstructure)",
            Build: contract => new MicropriceStrategy(contract)),
        new BacktestStrategyOption(
            Id: "ornsteinUhlenbeck",
            DisplayName: "Ornstein-Uhlenbeck mean reversion",
            Build: contract => new OrnsteinUhlenbeckStrategy(contract)),
        new BacktestStrategyOption(
            Id: "avellanedaStoikov",
            DisplayName: "Avellaneda-Stoikov market maker",
            Build: contract => new AvellanedaStoikovStrategy(contract)),
        new BacktestStrategyOption(
            Id: "twap",
            DisplayName: "TWAP buy execution",
            Build: contract => new TwapExecutionStrategy(contract, OrderSide.Buy)),
    };
}
