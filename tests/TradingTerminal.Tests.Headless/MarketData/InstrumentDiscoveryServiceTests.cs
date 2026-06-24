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
            new TradableInstrument("Apple", "US Equity", Contract.UsStock("AAPL"), BrokerKind.Alpaca),
            new TradableInstrument("Microsoft", "US Equity", Contract.UsStock("MSFT"), BrokerKind.Alpaca),
            new TradableInstrument("Nvidia", "US Equity", Contract.UsStock("NVDA"), BrokerKind.Alpaca),
        };

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.Alpaca);
        client.ListInstrumentsAsync(Arg.Any<CancellationToken>()).Returns(contracts);

        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        var selector = new FakeSelector(client, BrokerKind.Alpaca, state);
        var registry = Substitute.For<IInstrumentRegistry>();

        using var svc = new InstrumentDiscoveryService(
            selector, registry, NullLogger<InstrumentDiscoveryService>.Instance);
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
              .Returns(new[] { new TradableInstrument("X", "g", Contract.UsStock("X"), BrokerKind.InteractiveBrokers) });

        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        var selector = new FakeSelector(client, BrokerKind.InteractiveBrokers, state);
        var registry = Substitute.For<IInstrumentRegistry>();

        using var svc = new InstrumentDiscoveryService(
            selector, registry, NullLogger<InstrumentDiscoveryService>.Instance);
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

        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        var selector = new FakeSelector(client, BrokerKind.NinjaTrader, state);
        var registry = Substitute.For<IInstrumentRegistry>();

        using var svc = new InstrumentDiscoveryService(
            selector, registry, NullLogger<InstrumentDiscoveryService>.Instance);
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
        private readonly IBrokerClient _client;
        private readonly BrokerKind _kind;
        private readonly BehaviorSubject<ConnectionState> _state;

        public FakeSelector(IBrokerClient client, BrokerKind kind, BehaviorSubject<ConnectionState> state)
        {
            _client = client;
            _kind = kind;
            _state = state;
        }

        public IReadOnlyList<BrokerKind> AvailableKinds => new[] { _kind };
        public bool IsAvailable(BrokerKind kind) => kind == _kind;
        public IReadOnlyList<BrokerKind> Connected =>
            _state.Value == ConnectionState.Connected ? new[] { _kind } : Array.Empty<BrokerKind>();
        public bool IsConnected(BrokerKind kind) => kind == _kind && _state.Value == ConnectionState.Connected;
        public IBrokerClient Get(BrokerKind kind) => _client;
        public BrokerConnectionMode ModeOf(BrokerKind kind) => new(kind, false, "Test", "Test");
        public IObservable<ConnectionState> StateOf(BrokerKind kind) => _state;
        public ConnectionState CurrentStateOf(BrokerKind kind) => _state.Value;
        public event EventHandler<BrokerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
    }
}
