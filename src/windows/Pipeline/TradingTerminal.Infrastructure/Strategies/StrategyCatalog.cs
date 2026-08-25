using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Infrastructure.Strategies;

/// <summary>
/// Registers the strategy registry.
///
/// <para>The seed list is deliberately <b>empty</b>. It used to hold three demo strategies written
/// for the backtest engine (buy-and-hold, mean reversion, Donchian breakout); the engine was archived
/// on 2026-08-17 and they went with it. Every strategy now arrives at runtime — from an installed
/// <c>.daxalgostrategy</c> or from one authored in the app — and registers its own
/// <see cref="StrategyCatalogEntry"/>. <see cref="IStrategyRegistry"/> aggregates whatever
/// DI holds, so nothing needs naming here.</para>
///
/// <para>The seam survives the engine because it is what the <b>authoring</b> path registers into,
/// not because anything backtests. Naming is being settled with the compiler/Hyperion rework — see
/// issue #36.</para>
/// </summary>
public static class StrategyCatalog
{
    /// <summary>
    /// Wires the registry that aggregates every <see cref="StrategyCatalogEntry"/> in DI.
    /// View-models inject <see cref="IStrategyRegistry"/> rather than touching this.
    /// </summary>
    public static IServiceCollection AddStrategyCatalog(this IServiceCollection services)
    {
        services.AddSingleton<IStrategyRegistry, BacktestStrategyRegistry>();
        return services;
    }
}
