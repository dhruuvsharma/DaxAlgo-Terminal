using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Strategies.SigmaIcFlow.Engine;

namespace TradingTerminal.Strategies.SigmaIcFlow;

public static class DependencyInjection
{
    public static IServiceCollection AddSigmaIcFlowStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, SigmaIcFlowStrategy>();
        services.AddTransient<SigmaIcFlowStrategyViewModel>();

        // Backtest entry — the plugin owns its engine (ApexScalperStrategy, now in Engine/) and
        // registers its own BacktestStrategyOption at runtime. IBacktestStrategyRegistry aggregates
        // every DI-registered BacktestStrategyOption, so this needs NO edit to the host's
        // BacktestStrategyCatalog (where "sigmaIcFlow" used to be hardcoded). This is the plugin
        // model: a strategy registers into both seams (live IStrategyFactory + backtest registry)
        // from its own AddXxxStrategy() with no host recompile.
        services.AddSingleton(new BacktestStrategyOption(
            Id: "sigmaIcFlow",
            DisplayName: "Σ⁻¹·IC Order-Flow Optimizer (tape-primary, calibrated composite)",
            Build: contract => new ApexScalperStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.TradeTape | StrategyDataRequirement.L1 |
                              StrategyDataRequirement.Bars | StrategyDataRequirement.Depth,
            BacktestBuild = contract => new ApexScalperStrategy(contract, ApexV2Options.Backtest),
        });
#if WINDOWS
        services.AddTransient<SigmaIcFlowStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "sigma.ic.flow",
            ViewFactory: sp => sp.GetRequiredService<SigmaIcFlowStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<SigmaIcFlowStrategyViewModel>()));
#else
        services.AddTransient<AvaloniaUi.SigmaIcFlowStrategyAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "sigma.ic.flow",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.SigmaIcFlowStrategyAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<SigmaIcFlowStrategyViewModel>()));
#endif
        return services;
    }
}
