using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using Xunit;

namespace TradingTerminal.Tests.Ui;

/// <summary>
/// Proves the first ported per-strategy view-model resolves from the same headless DI graph the
/// Avalonia shell composes — i.e. the portable VM + its LiveStrategyHostServices bundle wire up
/// with no WPF. Runs headless on Windows and Linux.
/// </summary>
public sealed class PortedStrategyResolutionTests
{
    [Fact]
    public void OrnsteinUhlenbeck_view_model_resolves_from_the_headless_graph()
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

        // Not disposed on purpose: the headless graph's Rx-backed singletons (simulated broker /
        // selector) throw on synchronous teardown, which is irrelevant to this resolution check.
        var provider = services.BuildServiceProvider();

        var vm = provider.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>();

        vm.Should().NotBeNull();
        vm.StrategyId.Should().Be("ornstein.uhlenbeck");
        vm.AllInstruments.Should().NotBeEmpty("the VM seeds the shared instrument catalog");
    }
}
