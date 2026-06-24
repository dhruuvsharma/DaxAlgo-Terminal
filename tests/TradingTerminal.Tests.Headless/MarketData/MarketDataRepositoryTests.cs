using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Tests.TestSupport;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class MarketDataRepositoryTests
{
    private const BrokerKind TestBroker = BrokerKind.InteractiveBrokers;

    [Fact]
    public async Task Subscribe_routes_through_canonical_pipeline()
    {
        var contract = Contract.UsStock("NVDA");
        var instrumentId = new InstrumentId(42);

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract, TestBroker).Returns(instrumentId);
        var ingestHandle = Substitute.For<IDisposable>();
        ingest.Subscribe(contract, TestBroker).Returns(ingestHandle);

        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        var selector = new StubBrokerSelector(Substitute.For<IBrokerClient>());

        var repo = new MarketDataRepository(
            selector, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        using var cts = new CancellationTokenSource();
        var received = new List<Tick>();
        var loop = Task.Run(async () =>
        {
            try
            {
                await foreach (var tick in repo.SubscribeTicksAsync(contract, TestBroker, cts.Token))
                    received.Add(tick);
            }
            catch (OperationCanceledException) { /* expected */ }
        });

        // Deterministically wait until the background iterator has wired its hub subscription, then
        // publish exactly once. Polling on ingest.Subscribe (called during setup, before the hub
        // subscription) plus a brief settle avoids the fixed-delay race that was flaky under load/Linux.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000 &&
               ingest.ReceivedCalls().All(c => c.GetMethodInfo().Name != nameof(IMarketDataIngest.Subscribe)))
            await Task.Delay(10);
        await Task.Delay(20);

        hub.PublishQuote(new Quote(
            instrumentId, DateTime.UtcNow, DateTime.UtcNow,
            Bid: 100.0, Ask: 100.02, BidSize: 5, AskSize: 7,
            Source: BrokerKind.InteractiveBrokers, Sequence: 1, EventTimeApproximate: false));

        // Poll until the quote is observed (deterministic up to the timeout), rather than a fixed wait.
        sw.Restart();
        while (received.Count == 0 && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(10);

        cts.Cancel();
        await loop;

        ingest.Received(1).Resolve(contract, TestBroker);
        ingest.Received(1).Subscribe(contract, TestBroker);
        ingestHandle.Received(1).Dispose();
        received.Should().ContainSingle().Which.Bid.Should().Be(100.0);
    }

    [Fact]
    public async Task GetHistoricalBars_serves_fresh_cache_without_calling_broker()
    {
        var contract = Contract.UsStock("AAPL");
        var instrumentId = new InstrumentId(7);
        var size = BarSize.OneMinute;
        var duration = TimeSpan.FromMinutes(3);

        var now = DateTime.UtcNow;
        var cached = new[]
        {
            new OhlcvBar(instrumentId, size, now.AddMinutes(-3), 100, 101, 99.5, 100.5, 10, TestBroker, true),
            new OhlcvBar(instrumentId, size, now.AddMinutes(-2), 100.5, 102, 100.2, 101.7, 12, TestBroker, true),
            new OhlcvBar(instrumentId, size, now.AddMinutes(-1), 101.7, 102.3, 101.4, 102.1, 9, TestBroker, true),
        };

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract, TestBroker).Returns(instrumentId);
        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(instrumentId, size, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
             .Returns(cached);

        var client = Substitute.For<IBrokerClient>();
        var selector = new StubBrokerSelector(client);

        var repo = new MarketDataRepository(
            selector, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        var bars = await repo.GetHistoricalBarsAsync(contract, TestBroker, size, duration);

        bars.Should().HaveCount(3);
        bars[0].Close.Should().Be(100.5);
        await client.DidNotReceiveWithAnyArgs().RequestHistoricalBarsAsync(default!, default, default);
        store.DidNotReceiveWithAnyArgs().EnqueueBar(default!);
    }

    [Fact]
    public async Task GetHistoricalBars_fetches_from_broker_and_persists_on_cache_miss()
    {
        var contract = Contract.UsStock("AAPL");
        var instrumentId = new InstrumentId(7);
        var size = BarSize.OneMinute;
        var duration = TimeSpan.FromMinutes(3);

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract, TestBroker).Returns(instrumentId);
        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(instrumentId, size, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<OhlcvBar>());

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(TestBroker);
        client.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Connected));
        var freshBars = new[]
        {
            new Bar(DateTime.UtcNow.AddMinutes(-3), 100, 101, 99.5, 100.5, 10),
            new Bar(DateTime.UtcNow.AddMinutes(-2), 100.5, 102, 100.2, 101.7, 12),
            new Bar(DateTime.UtcNow.AddMinutes(-1), 101.7, 102.3, 101.4, 102.1, 9),
        };
        client.RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>())
              .Returns(freshBars);

        var selector = new StubBrokerSelector(client);

        var repo = new MarketDataRepository(
            selector, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        var bars = await repo.GetHistoricalBarsAsync(contract, TestBroker, size, duration);

        bars.Should().BeEquivalentTo(freshBars);
        await client.Received(1).RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>());
        store.Received(3).EnqueueBar(Arg.Is<OhlcvBar>(b =>
            b.InstrumentId == instrumentId && b.Size == size && b.IsFinal));
    }

    [Fact]
    public async Task GetHistoricalBars_refetches_when_cache_is_stale()
    {
        var contract = Contract.UsStock("AAPL");
        var instrumentId = new InstrumentId(7);
        var size = BarSize.OneMinute;
        var duration = TimeSpan.FromMinutes(3);

        var stale = DateTime.UtcNow.AddDays(-1);
        var cached = new[]
        {
            new OhlcvBar(instrumentId, size, stale.AddMinutes(-3), 100, 101, 99.5, 100.5, 10, TestBroker, true),
            new OhlcvBar(instrumentId, size, stale.AddMinutes(-2), 100.5, 102, 100.2, 101.7, 12, TestBroker, true),
            new OhlcvBar(instrumentId, size, stale.AddMinutes(-1), 101.7, 102.3, 101.4, 102.1, 9, TestBroker, true),
        };

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract, TestBroker).Returns(instrumentId);
        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(instrumentId, size, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
             .Returns(cached);

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(TestBroker);
        client.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Connected));
        var freshBars = new[]
        {
            new Bar(DateTime.UtcNow.AddMinutes(-3), 200, 201, 199.5, 200.5, 10),
            new Bar(DateTime.UtcNow.AddMinutes(-2), 200.5, 202, 200.2, 201.7, 12),
            new Bar(DateTime.UtcNow.AddMinutes(-1), 201.7, 202.3, 201.4, 202.1, 9),
        };
        client.RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>())
              .Returns(freshBars);

        var selector = new StubBrokerSelector(client);

        var repo = new MarketDataRepository(
            selector, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        var bars = await repo.GetHistoricalBarsAsync(contract, TestBroker, size, duration);

        bars[0].Open.Should().Be(200, because: "stale cache must be bypassed and fresh broker data returned");
        await client.Received(1).RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>());
    }

    /// <summary>Test selector that returns the single supplied client for whichever broker is requested.</summary>
    private sealed class StubBrokerSelector : IBrokerSelector
    {
        private readonly IBrokerClient _client;
        private readonly BehaviorSubject<ConnectionState> _state = new(ConnectionState.Connected);

        public StubBrokerSelector(IBrokerClient client)
        {
            _client = client;
            client.Kind.Returns(TestBroker);
            client.ConnectionState.Returns(_state);
        }

        public IReadOnlyList<BrokerKind> AvailableKinds => new[] { TestBroker };
        public bool IsAvailable(BrokerKind kind) => kind == TestBroker;
        public IReadOnlyList<BrokerKind> Connected => new[] { TestBroker };
        public bool IsConnected(BrokerKind kind) => kind == TestBroker;
        public IBrokerClient Get(BrokerKind kind) => _client;
        public BrokerConnectionMode ModeOf(BrokerKind kind) => new(kind, false, "Test", "Test");
        public IObservable<ConnectionState> StateOf(BrokerKind kind) => _state;
        public ConnectionState CurrentStateOf(BrokerKind kind) => _state.Value;
        public event EventHandler<BrokerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
    }
}
