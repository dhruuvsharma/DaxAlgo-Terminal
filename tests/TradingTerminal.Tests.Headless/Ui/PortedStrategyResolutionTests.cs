using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.FilteredOrderFlow;
using TradingTerminal.Strategies.ImbalanceHeatFront;
using TradingTerminal.Strategies.OrderFlowToxicity;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.Strategies.SigmaIcFlow;
using TradingTerminal.Strategies.VolatilityTargeted;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using Xunit;

namespace TradingTerminal.Tests.Ui;

/// <summary>
/// Proves the ported per-strategy view-models resolve from the same headless DI graph the Avalonia
/// shell composes — i.e. each portable VM + its LiveStrategyHostServices bundle wire up with no WPF.
/// Runs headless on Windows and Linux.
/// </summary>
public sealed class PortedStrategyResolutionTests
{
    private static IServiceProvider BuildHeadlessGraph()
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder().Build();
        services.AddSingleton(config);
        services.AddLogging();
        services.AddSingleton<InMemoryLogSink>();

        services.AddTradingTerminalInfrastructure();
        services.AddMarketDataPipeline(config);
        services.AddNotifications(config);

        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();
        services.AddSingleton(sp => new LiveStrategyHostServices(
            sp.GetRequiredService<IMarketDataRepository>(),
            sp.GetRequiredService<IMarketDataHub>(),
            sp.GetRequiredService<IMarketDataIngest>(),
            sp.GetRequiredService<IMarketDataStore>(),
            sp.GetRequiredService<IBrokerSelector>(),
            sp.GetRequiredService<InMemoryLogSink>()));

        services.AddOrnsteinUhlenbeckStrategy();
        services.AddCumulativeDeltaStrategy();
        services.AddVolatilityTargetedStrategy();
        services.AddOrderFlowToxicityStrategy();
        services.AddFilteredOrderFlowStrategy();
        services.AddImbalanceHeatFrontStrategy();
        services.AddSigmaIcFlowStrategy();

        // Not disposed on purpose: the headless graph's Rx-backed singletons (simulated broker /
        // selector) throw on synchronous teardown, which is irrelevant to these resolution checks.
        return services.BuildServiceProvider();
    }

    [Fact]
    public void OrnsteinUhlenbeck_view_model_resolves_with_seeded_instruments()
    {
        var vm = BuildHeadlessGraph().GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>();

        vm.StrategyId.Should().Be("ornstein.uhlenbeck");
        vm.AllInstruments.Should().NotBeEmpty("the VM seeds the shared instrument catalog");
    }

    [Fact]
    public void All_ported_strategy_view_models_resolve_from_the_headless_graph()
    {
        var provider = BuildHeadlessGraph();

        provider.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>().Should().NotBeNull();
        provider.GetRequiredService<CumulativeDeltaViewModel>().Should().NotBeNull();
        provider.GetRequiredService<VolatilityTargetedStrategyViewModel>().Should().NotBeNull();
        provider.GetRequiredService<OrderFlowToxicityStrategyViewModel>().Should().NotBeNull();
        provider.GetRequiredService<FilteredOrderFlowViewModel>().Should().NotBeNull();
        provider.GetRequiredService<ImbalanceHeatFrontViewModel>().Should().NotBeNull();
        provider.GetRequiredService<SigmaIcFlowStrategyViewModel>().Should().NotBeNull();
    }
}
