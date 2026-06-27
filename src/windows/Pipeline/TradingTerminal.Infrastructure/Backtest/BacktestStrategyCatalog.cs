using System.Globalization;
using System.Linq;
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
            Fast: true)
        {
            WalkForwardGrid = axes =>
                (from l in WalkForwardAxes.Or(axes.Lookbacks, 50, 100, 200)
                 from e in WalkForwardAxes.Or(axes.Entries, 0.05, 0.10, 0.20)
                 from s in WalkForwardAxes.Or(axes.Stops, 0.20, 0.40)
                 select new WalkForwardCandidate(
                     $"mr-lk{l}-e{e.ToString(CultureInfo.InvariantCulture)}-s{s.ToString(CultureInfo.InvariantCulture)}",
                     c => new MeanReversionStrategy(c, l, e, s, axes.Quantity))).ToList(),
        },
        new BacktestStrategyOption(
            Id: "donchianBreakout",
            DisplayName: "Donchian breakout (demo)",
            Build: contract => new DonchianBreakoutStrategy(contract))
        {
            WalkForwardGrid = axes =>
                (from l in WalkForwardAxes.Or(axes.Lookbacks, 50, 100, 200)
                 from t in WalkForwardAxes.Or(axes.Trails, 0.10, 0.20, 0.40)
                 select new WalkForwardCandidate(
                     $"don-lk{l}-trail{t.ToString(CultureInfo.InvariantCulture)}",
                     c => new DonchianBreakoutStrategy(c, l, t, axes.Quantity))).ToList(),
        },
        new BacktestStrategyOption(
            Id: "ornsteinUhlenbeck",
            DisplayName: "Ornstein-Uhlenbeck mean reversion",
            Build: contract => new OrnsteinUhlenbeckStrategy(contract))
        {
            WalkForwardGrid = axes =>
                (from l in WalkForwardAxes.Or(axes.Lookbacks, 300, 500, 1000)
                 from z in WalkForwardAxes.Or(axes.EntryZ, 1.5, 2.0, 2.5)
                 select new WalkForwardCandidate(
                     $"ou-lk{l}-z{z.ToString(CultureInfo.InvariantCulture)}",
                     c => new OrnsteinUhlenbeckStrategy(c, lookback: l, entryZ: z, quantity: axes.Quantity))).ToList(),
        },
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
        // NOTE: "sigmaIcFlow" is no longer registered here. Its engine (ApexScalperStrategy) now
        // lives in the TradingTerminal.Strategies.SigmaIcFlow plugin and registers its own
        // BacktestStrategyOption at runtime via IBacktestStrategyRegistry in AddSigmaIcFlowStrategy().
        // This is the plugin model: the host catalog names no plugin-owned strategy.
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
