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
        // NOTE: the host catalog now names only the three engine demos. Every first-party strategy
        // (SigmaIcFlow, IndexRegimeGraph, OrderFlowCube, OrderFlowSurfaceSpike, ImbalanceHeatFront,
        // IndexKScoreSurface, FilteredOrderFlow, …) ships as an EXTERNAL plugin: each moves its engine
        // into its own project and registers its BacktestStrategyOption at runtime via
        // Add<Name>Strategy(). IBacktestStrategyRegistry aggregates every DI-registered option, so no
        // plugin-owned strategy is named here.
    };
}
