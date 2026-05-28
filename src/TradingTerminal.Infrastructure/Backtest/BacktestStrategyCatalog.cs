using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Seed list of strategies the backtest engine knows about. Held here as a
/// <see cref="IReadOnlyList{T}"/> rather than as injected singletons so the list is
/// usable both at DI-registration time (for the signal-host loop in
/// <c>SignalStrategiesRegistration</c>, which runs before the service provider exists)
/// and at runtime (via <see cref="IBacktestStrategyRegistry"/>).
///
/// Adding a strategy: append to <see cref="All"/>. The registry rebuilds on next start.
/// Future work could register options dynamically through DI; today the catalog stays
/// canonical to keep the registration story simple.
/// </summary>
public static class BacktestStrategyCatalog
{
    /// <summary>
    /// Wires the registry: registers each catalog entry as a <see cref="BacktestStrategyOption"/>
    /// singleton plus the default <see cref="IBacktestStrategyRegistry"/> that aggregates them.
    /// View-models inject <c>IBacktestStrategyRegistry</c> instead of touching this static.
    /// </summary>
    public static IServiceCollection AddBacktestStrategyCatalog(this IServiceCollection services)
    {
        foreach (var option in All)
            services.AddSingleton(option);
        services.AddSingleton<IBacktestStrategyRegistry, BacktestStrategyRegistry>();
        return services;
    }

    public static IReadOnlyList<BacktestStrategyOption> All { get; } = new[]
    {
        new BacktestStrategyOption(
            Id: "buyAndHold",
            DisplayName: "Buy & hold (demo)",
            Build: contract => new BuyAndHoldStrategy(contract)),
        new BacktestStrategyOption(
            Id: "meanReversion",
            DisplayName: "Mean reversion (demo)",
            Build: contract => new MeanReversionStrategy(contract),
            Fast: true),
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

        // ── L2 / depth-of-market themed (cTrader DOM territory) ───────────────────────
        new BacktestStrategyOption(
            Id: "bookPressure",
            DisplayName: "Order-book pressure / cumulative imbalance (L2)",
            Build: contract => new BookPressureStrategy(contract)),
        new BacktestStrategyOption(
            Id: "liquiditySweep",
            DisplayName: "Liquidity-sweep / aggressive-flow detector (L2)",
            Build: contract => new LiquiditySweepStrategy(contract)),
        new BacktestStrategyOption(
            Id: "iceberg",
            DisplayName: "Iceberg / hidden-liquidity detector (L2)",
            Build: contract => new IcebergDetectionStrategy(contract)),
        new BacktestStrategyOption(
            Id: "vpin",
            DisplayName: "Order-flow toxicity / VPIN-style (L2)",
            Build: contract => new OrderFlowToxicityStrategy(contract)),
        new BacktestStrategyOption(
            Id: "orderFlowCube",
            DisplayName: "Order-flow regime cube (CVD × aggressor × size)",
            Build: contract => new OrderFlowCubeStrategy(contract)),
        new BacktestStrategyOption(
            Id: "orderFlowSurfaceSpike",
            DisplayName: "Order-flow surface spike detector (Z-score over slice×bin matrix)",
            Build: contract => new OrderFlowSurfaceSpikeStrategy(contract)),
        new BacktestStrategyOption(
            Id: "imbalanceHeatFront",
            DisplayName: "Imbalance Heat Front (L2 bid-ask pressure surface)",
            Build: contract => new ImbalanceHeatFrontStrategy(contract)),
        new BacktestStrategyOption(
            Id: "thinBook",
            DisplayName: "Thin-book breakout filter (L2)",
            Build: contract => new ThinBookFilterStrategy(contract)),
        new BacktestStrategyOption(
            Id: "apexScalper",
            DisplayName: "APEX microstructure scalper (composite, 8 signals)",
            Build: contract => new ApexScalperStrategy(contract)),
        new BacktestStrategyOption(
            Id: "indexKScoreSurface",
            DisplayName: "Index K-Score Surface (single-instrument backtest variant)",
            Build: contract => new IndexKScoreSurfaceStrategy(contract)),

        // ── ML / AI driven ────────────────────────────────────────────────────────────
        new BacktestStrategyOption(
            Id: "onlineRegressionAlpha",
            DisplayName: "Online-regression alpha (RLS)",
            Build: contract => new OnlineRegressionAlphaStrategy(contract)),
        new BacktestStrategyOption(
            Id: "anomalyDetector",
            DisplayName: "Rolling z-score anomaly detector",
            Build: contract => new AnomalyDetectorStrategy(contract)),
    };
}
