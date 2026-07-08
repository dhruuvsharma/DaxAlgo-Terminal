using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Brokers.CTrader;
using TradingTerminal.Core.Brokers.Upstox;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Login;
using Xunit;

namespace TradingTerminal.Tests.Login;

/// <summary>
/// Guards the login-form ⇄ broker-registration pairing behind the edition split. Resolving
/// <c>IEnumerable&lt;IBrokerLoginForm&gt;</c> instantiates every registered form, so a form whose
/// broker services aren't registered crashes the login window at composition time — exactly what
/// happened when the keyless-only Basic shell carried the cTrader form (needs
/// <c>ICTraderAccountDiscovery</c>, which only <c>AddCredentialedBrokers()</c> provides).
/// These tests compose the login layer the way each edition shell does — without booting WPF —
/// and pin the resolved form set to <see cref="BrokerEditionPolicy"/>.
/// </summary>
public sealed class LoginFormEditionCompositionTests
{
    /// <summary>The broker-neutral services every edition provides before AddLogin.</summary>
    private static ServiceCollection CommonServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddOptions();
        services.AddSingleton(Substitute.For<IBrokerSelector>());
        return services;
    }

    [Fact]
    public void Basic_composition_keyless_forms_resolve_without_credentialed_broker_services()
    {
        // The Basic shell: AddLogin() only — no credentialed brokers, no credentialed forms.
        var services = CommonServices();
        services.AddLogin();

        using var provider = services.BuildServiceProvider();

        // The old single AddLogin() registered every form here, so this resolution threw
        // InvalidOperationException (cTrader's ICTraderAccountDiscovery unresolvable).
        var forms = provider.GetServices<IBrokerLoginForm>().ToList();

        forms.Select(f => f.Broker).Should().BeEquivalentTo(
            BrokerEditionPolicy.Keyless.Where(k => k != BrokerKind.Simulated),
            "AddLogin must carry exactly the keyless-broker forms (Simulated has no login form)");
    }

    [Fact]
    public void Intermediate_composition_resolves_every_form()
    {
        // Intermediate / Professional: AddLogin() + AddCredentialedLoginForms(), alongside the
        // credentialed broker services those forms consume (substituted here — the real ones come
        // from AddCredentialedBrokers).
        var services = CommonServices();
        services.AddSingleton(Substitute.For<ICTraderAccountDiscovery>());
        services.AddSingleton(Substitute.For<IUpstoxAuthService>());
        services.AddLogin();
        services.AddCredentialedLoginForms();

        using var provider = services.BuildServiceProvider();

        var forms = provider.GetServices<IBrokerLoginForm>().ToList();

        var expected = BrokerEditionPolicy.Keyless
            .Where(k => k != BrokerKind.Simulated)
            .Concat(BrokerEditionPolicy.Credentialed);
        forms.Select(f => f.Broker).Should().BeEquivalentTo(expected,
            "the full login must carry one form per keyless + credentialed broker");
    }

    [Fact]
    public void Every_form_broker_is_classified_by_the_edition_policy()
    {
        // Drift guard: a new login form must land in exactly one BrokerEditionPolicy bucket, so
        // the per-edition shells know which AddXxxLoginForms call ships it.
        var services = CommonServices();
        services.AddSingleton(Substitute.For<ICTraderAccountDiscovery>());
        services.AddSingleton(Substitute.For<IUpstoxAuthService>());
        services.AddLogin();
        services.AddCredentialedLoginForms();

        using var provider = services.BuildServiceProvider();

        foreach (var form in provider.GetServices<IBrokerLoginForm>())
        {
            var keyless = BrokerEditionPolicy.Keyless.Contains(form.Broker);
            var credentialed = BrokerEditionPolicy.Credentialed.Contains(form.Broker);
            (keyless ^ credentialed).Should().BeTrue(
                $"broker {form.Broker} must be in exactly one of BrokerEditionPolicy.Keyless / .Credentialed");
        }
    }
}
