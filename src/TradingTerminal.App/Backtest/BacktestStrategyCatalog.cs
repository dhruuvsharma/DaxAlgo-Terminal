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

        // ── Forex baselines ────────────────────────────────────────────────────────────
        new BacktestStrategyOption(
            Id: "bollinger",
            DisplayName: "Bollinger band reversion (forex)",
            Build: contract => new BollingerReversionStrategy(contract)),
        new BacktestStrategyOption(
            Id: "maCrossover",
            DisplayName: "MA crossover / golden cross (forex)",
            Build: contract => new MovingAverageCrossoverStrategy(contract)),
        new BacktestStrategyOption(
            Id: "rsi2",
            DisplayName: "Connors RSI(2) reversion (forex)",
            Build: contract => new RsiTwoPeriodStrategy(contract)),
        new BacktestStrategyOption(
            Id: "londonOpen",
            DisplayName: "London-open breakout (forex)",
            Build: contract => new LondonOpenBreakoutStrategy(contract)),
        new BacktestStrategyOption(
            Id: "macd",
            DisplayName: "MACD signal crossover (forex)",
            Build: contract => new MacdCrossoverStrategy(contract)),

        // ── S&P 500 / index baselines ─────────────────────────────────────────────────
        new BacktestStrategyOption(
            Id: "trendFilter",
            DisplayName: "200-SMA trend filter (index)",
            Build: contract => new TrendFilterStrategy(contract)),
        new BacktestStrategyOption(
            Id: "volTarget",
            DisplayName: "Volatility targeting (index)",
            Build: contract => new VolatilityTargetedStrategy(contract)),
        new BacktestStrategyOption(
            Id: "gapFade",
            DisplayName: "Overnight gap fade (index)",
            Build: contract => new GapFadeStrategy(contract)),
        new BacktestStrategyOption(
            Id: "eodMomentum",
            DisplayName: "End-of-day momentum (index)",
            Build: contract => new EndOfDayMomentumStrategy(contract)),
        new BacktestStrategyOption(
            Id: "pullback",
            DisplayName: "Trend pullback continuation (index)",
            Build: contract => new PullbackContinuationStrategy(contract)),
    };
}
