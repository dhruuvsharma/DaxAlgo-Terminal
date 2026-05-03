using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Example;

public static class DependencyInjection
{
    /// <summary>
    /// Plug-in entry point. Adding a new strategy = new project + one
    /// <c>services.AddXxxStrategy()</c> call. The shell never references the strategy type directly.
    /// </summary>
    public static IServiceCollection AddExampleStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, ExampleStrategy>();
        services.AddTransient<ExampleStrategyViewModel>();
        services.AddTransient<ExampleStrategyView>();

        // Strategy factory looks up registrations by id and resolves the (view, vm) pair.
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "example.nvda.3m",
            ViewFactory: sp => sp.GetRequiredService<ExampleStrategyView>(),
            ViewModelFactory: sp => sp.GetRequiredService<ExampleStrategyViewModel>()));

        return services;
    }
}
