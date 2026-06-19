using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
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
            Id: "ornsteinUhlenbeck",
            DisplayName: "Ornstein-Uhlenbeck mean reversion",
            Build: contract => new OrnsteinUhlenbeckStrategy(contract)),
        // ── S&P 500 / index baselines ─────────────────────────────────────────────────
        new BacktestStrategyOption(
            Id: "volTarget",
            DisplayName: "Volatility targeting (index)",
            Build: contract => new VolatilityTargetedStrategy(contract)),

        // ── L2 / depth-of-market themed (cTrader DOM territory) ───────────────────────
        new BacktestStrategyOption(
            Id: "vpin",
            DisplayName: "Order-flow toxicity / VPIN-style (L1 approx.)",
            Build: contract => new OrderFlowToxicityStrategy(contract)),
        new BacktestStrategyOption(
            Id: "orderFlowCube",
            DisplayName: "Order-flow regime cube (CVD × aggressor × size)",
            Build: contract => new OrderFlowCubeStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        },
        new BacktestStrategyOption(
            Id: "orderFlowSurfaceSpike",
            DisplayName: "Order-flow surface spike detector (Z-score over slice×bin matrix)",
            Build: contract => new OrderFlowSurfaceSpikeStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        },
        new BacktestStrategyOption(
            Id: "imbalanceHeatFront",
            DisplayName: "Imbalance Heat Front (L2 bid-ask pressure surface)",
            Build: contract => new ImbalanceHeatFrontStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth,
        },
        new BacktestStrategyOption(
            Id: "sigmaIcFlow",
            DisplayName: "Σ⁻¹·IC Order-Flow Optimizer (tape-primary, calibrated composite)",
            Build: contract => new ApexScalperStrategy(contract))
        {
            // v2 is trade-tape primary; L1 quotes drive the synthetic fallback and spread, depth
            // is optional (OBI participates only when a genuine depth stream is live and fresh).
            DataRequirement = StrategyDataRequirement.TradeTape | StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth,
        },
        new BacktestStrategyOption(
            Id: "indexKScoreSurface",
            DisplayName: "Index K-Score Surface (single-instrument backtest variant)",
            Build: contract => new IndexKScoreSurfaceStrategy(contract)),
        new BacktestStrategyOption(
            Id: "filteredOrderFlow",
            DisplayName: "Filtered order-flow imbalance OBI(T) (arXiv:2507.22712)",
            Build: contract => new FilteredOrderFlowStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        },
    };
}
