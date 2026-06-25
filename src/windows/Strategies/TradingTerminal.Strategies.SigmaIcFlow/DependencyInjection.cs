using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.SigmaIcFlow;

public static class DependencyInjection
{
    public static IServiceCollection AddSigmaIcFlowStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, SigmaIcFlowStrategy>();
        services.AddTransient<SigmaIcFlowStrategyViewModel>();
#if WINDOWS
        services.AddTransient<SigmaIcFlowStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "sigma.ic.flow",
            ViewFactory: sp => sp.GetRequiredService<SigmaIcFlowStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<SigmaIcFlowStrategyViewModel>()));
#else
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "sigma.ic.flow",
            ViewFactory: _ => new global::TradingTerminal.UI.Avalonia.GenericStrategyWindow(),
            ViewModelFactory: sp => sp.GetRequiredService<SigmaIcFlowStrategyViewModel>()));
#endif
        return services;
    }
}
