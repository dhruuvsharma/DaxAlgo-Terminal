using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class MarketDataIngestServiceTests
{
    [Theory]
    [InlineData(BrokerKind.Alpaca, false)]     // Alpaca stamps real exchange time
    [InlineData(BrokerKind.CTrader, true)]     // cTrader stamps local arrival time → approximate
    public async Task Ticks_are_normalized_to_quotes_with_provenance(BrokerKind broker, bool expectApprox)
    {
        var id = new InstrumentId(42);
        var hub = new MarketDataHub();

        var registry = Substitute.For<IInstrumentRegistry>();
        registry.ResolveOrCreate(Arg.Any<Contract>(), Arg.Any<BrokerKind>()).Returns(id);

        var store = Substitute.For<IMarketDataStore>();

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(broker);
        client.SubscribeTicksAsync(Arg.Any<Contract>(), Arg.Any<CancellationToken>()).Returns(TwoTicks());
        client.SubscribeDepthAsync(Arg.Any<Contract>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(NoDepth());

        var selector = new FakeSelector(client, broker);
        var ingest = new MarketDataIngestService(selector, registry, hub, store, NullLogger<MarketDataIngestService>.Instance);

        var got = new List<Quote>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = hub.Quotes(id).Subscribe(q => { got.Add(q); if (got.Count == 2) done.TrySetResult(); });

        using var handle = ingest.Subscribe(Contract.UsStock("AAPL"), broker);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        got.Should().HaveCount(2);
        got[0].Source.Should().Be(broker);
        got[0].EventTimeApproximate.Should().Be(expectApprox);
        got[0].Sequence.Should().Be(1);
        got[1].Sequence.Should().Be(2);  // per-instrument monotonic
        store.Received(2).EnqueueQuote(Arg.Any<Quote>());
    }

    [Fact]
    public async Task Refcounted_subscription_starts_one_feed_for_two_subscribers()
    {
        var id = new InstrumentId(7);
        var hub = new MarketDataHub();
        var registry = Substitute.For<IInstrumentRegistry>();
        registry.ResolveOrCreate(Arg.Any<Contract>(), Arg.Any<BrokerKind>()).Returns(id);
        var store = Substitute.For<IMarketDataStore>();

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.Alpaca);
        client.SubscribeTicksAsync(Arg.Any<Contract>(), Arg.Any<CancellationToken>()).Returns(_ => TwoTicks());
        client.SubscribeDepthAsync(Arg.Any<Contract>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => NoDepth());

        var selector = new FakeSelector(client, BrokerKind.Alpaca);
        var ingest = new MarketDataIngestService(selector, registry, hub, store, NullLogger<MarketDataIngestService>.Instance);

        var c = Contract.UsStock("AAPL");
        using (var h1 = ingest.Subscribe(c, BrokerKind.Alpaca))
        using (var h2 = ingest.Subscribe(c, BrokerKind.Alpaca))
        {
            await Task.Delay(50);
            // Both handles share one broker tick subscription.
            client.Received(1).SubscribeTicksAsync(Arg.Any<Contract>(), Arg.Any<CancellationToken>());
        }
    }

    private static async IAsyncEnumerable<Tick> TwoTicks([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new Tick(DateTime.UtcNow, 100, 101, 1, 2);
        yield return new Tick(DateTime.UtcNow, 100.1, 101.1, 3, 4);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<DepthSnapshot> NoDepth([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class FakeSelector : IBrokerSelector
    {
        private readonly IBrokerClient _client;
        private readonly BrokerKind _kind;
        private readonly BehaviorSubject<ConnectionState> _state = new(ConnectionState.Connected);

        public FakeSelector(IBrokerClient client, BrokerKind kind)
        {
            _client = client;
            _kind = kind;
        }

        public IReadOnlyList<BrokerKind> AvailableKinds => new[] { _kind };
        public bool IsAvailable(BrokerKind kind) => kind == _kind;
        public IReadOnlyList<BrokerKind> Connected => new[] { _kind };
        public bool IsConnected(BrokerKind kind) => kind == _kind;
        public IBrokerClient Get(BrokerKind kind) => _client;
        public BrokerConnectionMode ModeOf(BrokerKind kind) => new(kind, false, "Test", "Test");
        public IObservable<ConnectionState> StateOf(BrokerKind kind) => _state;
        public ConnectionState CurrentStateOf(BrokerKind kind) => _state.Value;
        public event EventHandler<BrokerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
    }
}
