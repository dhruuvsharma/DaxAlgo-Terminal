using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.BookPressure;

public static class DependencyInjection
{
    public static IServiceCollection AddBookPressureStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, BookPressureStrategy>();
        services.AddTransient<BookPressureStrategyViewModel>();
        services.AddTransient<BookPressureStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "book.pressure",
            ViewFactory: sp => sp.GetRequiredService<BookPressureStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<BookPressureStrategyViewModel>()));
        return services;
    }
}