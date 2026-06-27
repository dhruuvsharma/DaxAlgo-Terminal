using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrnsteinUhlenbeck;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the strategy descriptor + the (portable) live VM on every target, and — only on the
    /// WPF build — the MahApps window and its catalog <see cref="StrategyFactoryRegistration"/>. The
    /// Avalonia head registers its own view against the same VM.
    /// </summary>
    public static IServiceCollection AddOrnsteinUhlenbeckStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrnsteinUhlenbeckStrategy>();
        services.AddTransient<OrnsteinUhlenbeckStrategyViewModel>();
#if WINDOWS
        services.AddTransient<OrnsteinUhlenbeckStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "ornstein.uhlenbeck",
            ViewFactory: sp => sp.GetRequiredService<OrnsteinUhlenbeckStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>()));
#else
        // Cross-platform (Avalonia) leg: register the Avalonia window against the same VM, so the
        // Avalonia shell opens it through IStrategyFactory.Create("ornstein.uhlenbeck").
        services.AddTransient<AvaloniaUi.OrnsteinUhlenbeckAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "ornstein.uhlenbeck",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.OrnsteinUhlenbeckAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>()));
#endif
        return services;
    }
}