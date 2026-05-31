using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Login;

/// <summary>
/// DI registration for the login flow. The shell composition root calls <see cref="AddLogin"/>
/// once; the App project never references the concrete login window / form view-models directly.
/// The shell-handoff factory (<c>ILoginShellFactory</c>) stays in the App project since it builds
/// the main window after a successful sign-in.
/// </summary>
public static class LoginServiceCollectionExtensions
{
    public static IServiceCollection AddLogin(this IServiceCollection services)
    {
        services.AddSingleton<CredentialStore>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();

        // Per-broker login forms. Each form is registered as both its concrete type (for the
        // factory's GetRequiredService lookup) and as IBrokerLoginForm (so the factory can
        // enumerate them).
        services.AddSingleton<IbLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<IbLoginFormViewModel>());

        services.AddSingleton<NinjaLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<NinjaLoginFormViewModel>());

        services.AddSingleton<CTraderLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<CTraderLoginFormViewModel>());

        services.AddSingleton<AlpacaLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<AlpacaLoginFormViewModel>());

        services.AddSingleton<IBrokerLoginFormFactory, BrokerLoginFormFactory>();
        return services;
    }
}
