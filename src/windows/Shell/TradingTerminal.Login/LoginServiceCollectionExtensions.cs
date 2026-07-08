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
/// <remarks>
/// Form registration mirrors the broker-client split (<c>AddKeylessBrokers</c> /
/// <c>AddCredentialedBrokers</c> in Infrastructure): <see cref="AddLogin"/> carries only the
/// keyless-broker forms, and a shell that registers the credentialed brokers pairs it with
/// <see cref="AddCredentialedLoginForms"/>. The pairing matters because resolving
/// <c>IEnumerable&lt;IBrokerLoginForm&gt;</c> instantiates every registered form — a credentialed
/// form whose broker services are absent (e.g. cTrader's <c>ICTraderAccountDiscovery</c> in the
/// keyless-only Basic edition) would crash the login window at composition time.
/// </remarks>
public static class LoginServiceCollectionExtensions
{
    /// <summary>The login window/flow plus the KEYLESS broker forms (public crypto feeds — no API
    /// key, no account). Every edition shell calls this.</summary>
    public static IServiceCollection AddLogin(this IServiceCollection services)
    {
        services.AddSingleton<CredentialStore>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();

        // Per-broker login forms. Each form is registered as both its concrete type (for the
        // factory's GetRequiredService lookup) and as IBrokerLoginForm (so the factory can
        // enumerate them).
        services.AddSingleton<BinanceLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<BinanceLoginFormViewModel>());

        services.AddSingleton<CoinbaseLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<CoinbaseLoginFormViewModel>());

        services.AddSingleton<BybitLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<BybitLoginFormViewModel>());

        services.AddSingleton<KrakenLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<KrakenLoginFormViewModel>());

        services.AddSingleton<OkxLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<OkxLoginFormViewModel>());

        services.AddSingleton<IBrokerLoginFormFactory, BrokerLoginFormFactory>();
        return services;
    }

    /// <summary>The CREDENTIALED broker forms (IB / NinjaTrader / cTrader / Alpaca / Ironbeam /
    /// LSE / Upstox). Call from every shell that also calls <c>AddCredentialedBrokers()</c>
    /// (Intermediate and Professional); the keyless-only Basic shell must not, because these
    /// forms resolve broker services only the credentialed registration provides.</summary>
    public static IServiceCollection AddCredentialedLoginForms(this IServiceCollection services)
    {
        services.AddSingleton<IbLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<IbLoginFormViewModel>());

        services.AddSingleton<NinjaLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<NinjaLoginFormViewModel>());

        services.AddSingleton<CTraderLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<CTraderLoginFormViewModel>());

        services.AddSingleton<AlpacaLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<AlpacaLoginFormViewModel>());

        services.AddSingleton<IronBeamLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<IronBeamLoginFormViewModel>());

        services.AddSingleton<LondonStrategicEdgeLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<LondonStrategicEdgeLoginFormViewModel>());

        services.AddSingleton<UpstoxLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<UpstoxLoginFormViewModel>());

        return services;
    }
}
