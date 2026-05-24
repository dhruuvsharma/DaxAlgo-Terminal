using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class InstrumentDiscoveryServiceTests
{
    [Fact]
    public async Task On_connected_state_loads_universe_and_registers_each_contract()
    {
        var contracts = new[]
        {
            new TradableInstrument("Apple", "US Equity", Contract.UsStock("AAPL")),
            new TradableInstrument("Microsoft", "US Equity", Contract.UsStock("MSFT")),
            new TradableInstrument("Nvidia", "US Equity", Contract.UsStock("NVDA")),
        };

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.Alpaca);
        client.ListInstrumentsAsync(Arg.Any<CancellationToken>()).Returns(contracts);

        var selector = new FakeSelector(client);
        var registry = Substitute.For<IInstrumentRegistry>();
        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);

        using var svc = new InstrumentDiscoveryService(
            selector, state, registry, NullLogger<InstrumentDiscoveryService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        // Trigger discovery by transitioning to Connected.
        state.OnNext(ConnectionState.Connected);

        // Discovery runs on a background task; poll briefly for completion.
        await WaitForAsync(() => registry.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IInstrumentRegistry.ResolveOrCreate)) == 3);

        registry.Received(1).ResolveOrCreate(contracts[0].Contract, BrokerKind.Alpaca);
        registry.Received(1).ResolveOrCreate(contracts[1].Contract, BrokerKind.Alpaca);
        registry.Received(1).ResolveOrCreate(contracts[2].Contract, BrokerKind.Alpaca);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Does_not_register_anything_before_Connected_fires()
    {
        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.InteractiveBrokers);
        client.ListInstrumentsAsync(Arg.Any<CancellationToken>())
              .Returns(new[] { new TradableInstrument("X", "g", Contract.UsStock("X")) });

        var selector = new FakeSelector(client);
        var registry = Substitute.For<IInstrumentRegistry>();
        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);

        using var svc = new InstrumentDiscoveryService(
            selector, state, registry, NullLogger<InstrumentDiscoveryService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        state.OnNext(ConnectionState.Connecting);
        state.OnNext(ConnectionState.Reconnecting);
        state.OnNext(ConnectionState.Failed);
        await Task.Delay(50);

        registry.DidNotReceiveWithAnyArgs().ResolveOrCreate(default!, default);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Handles_broker_returning_empty_universe()
    {
        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.NinjaTrader);
        client.ListInstrumentsAsync(Arg.Any<CancellationToken>())
              .Returns(Array.Empty<TradableInstrument>());

        var selector = new FakeSelector(client);
        var registry = Substitute.For<IInstrumentRegistry>();
        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);

        using var svc = new InstrumentDiscoveryService(
            selector, state, registry, NullLogger<InstrumentDiscoveryService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        state.OnNext(ConnectionState.Connected);
        await Task.Delay(50);

        registry.DidNotReceiveWithAnyArgs().ResolveOrCreate(default!, default);
        await svc.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 2000, int pollMs = 10)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(pollMs);
        }
        predicate().Should().BeTrue("expected condition to hold within timeout");
    }

    private sealed class FakeSelector : IBrokerSelector
    {
        public FakeSelector(IBrokerClient client)
        {
            Active = client;
            ActiveMode = new BrokerConnectionMode(client.Kind, false, "Test", "Test");
        }

        public BrokerKind ActiveKind => Active.Kind;
        public IBrokerClient Active { get; }
        public BrokerConnectionMode ActiveMode { get; }
        public IReadOnlyList<BrokerKind> AvailableKinds => new[] { Active.Kind };
        public bool IsAvailable(BrokerKind kind) => kind == Active.Kind;
        public event EventHandler? ActiveChanged { add { } remove { } }
        public void SetActive(BrokerKind kind) { }
    }
}
