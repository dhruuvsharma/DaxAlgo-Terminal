using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Tests.TestSupport;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class MarketDataRepositoryTests
{
    [Fact]
    public async Task Subscribe_routes_through_canonical_pipeline()
    {
        // SubscribeTicksAsync should resolve the canonical id, subscribe to hub.Quotes(id),
        // and turn on the ref-counted broker pump via ingest.Subscribe — the strategy never
        // touches the broker directly anymore.
        var contract = Contract.UsStock("NVDA");
        var instrumentId = new InstrumentId(42);

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract).Returns(instrumentId);
        var ingestHandle = Substitute.For<IDisposable>();
        ingest.Subscribe(contract).Returns(ingestHandle);

        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        var selector = new SingleClientSelector(Substitute.For<IBrokerClient>());
        var connection = new ConnectionManager(selector, NullLogger<ConnectionManager>.Instance);

        var repo = new MarketDataRepository(
            selector, connection, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        using var cts = new CancellationTokenSource();
        var received = new List<Tick>();
        var loop = Task.Run(async () =>
        {
            try
            {
                await foreach (var tick in repo.SubscribeTicksAsync(contract, cts.Token))
                    received.Add(tick);
            }
            catch (OperationCanceledException) { /* expected */ }
        });

        // Publishing a canonical Quote to the hub must reach the consumer as a legacy Tick.
        await Task.Delay(20);
        hub.PublishQuote(new Quote(
            instrumentId, DateTime.UtcNow, DateTime.UtcNow,
            Bid: 100.0, Ask: 100.02, BidSize: 5, AskSize: 7,
            Source: BrokerKind.InteractiveBrokers, Sequence: 1, EventTimeApproximate: false));

        await Task.Delay(20);
        cts.Cancel();
        await loop;

        ingest.Received(1).Resolve(contract);
        ingest.Received(1).Subscribe(contract);
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

        // Three 1-min bars ending right now → cache is full + fresh; broker must NOT be hit.
        var now = DateTime.UtcNow;
        var cached = new[]
        {
            new OhlcvBar(instrumentId, size, now.AddMinutes(-3), 100, 101, 99.5, 100.5, 10, BrokerKind.InteractiveBrokers, true),
            new OhlcvBar(instrumentId, size, now.AddMinutes(-2), 100.5, 102, 100.2, 101.7, 12, BrokerKind.InteractiveBrokers, true),
            new OhlcvBar(instrumentId, size, now.AddMinutes(-1), 101.7, 102.3, 101.4, 102.1, 9, BrokerKind.InteractiveBrokers, true),
        };

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract).Returns(instrumentId);
        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(instrumentId, size, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(cached);

        var client = Substitute.For<IBrokerClient>();
        var selector = new SingleClientSelector(client);
        var connection = new ConnectionManager(selector, NullLogger<ConnectionManager>.Instance);

        var repo = new MarketDataRepository(
            selector, connection, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        var bars = await repo.GetHistoricalBarsAsync(contract, size, duration);

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
        ingest.Resolve(contract).Returns(instrumentId);
        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(instrumentId, size, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<OhlcvBar>());

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.InteractiveBrokers);
        client.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Connected));
        var freshBars = new[]
        {
            new Bar(DateTime.UtcNow.AddMinutes(-3), 100, 101, 99.5, 100.5, 10),
            new Bar(DateTime.UtcNow.AddMinutes(-2), 100.5, 102, 100.2, 101.7, 12),
            new Bar(DateTime.UtcNow.AddMinutes(-1), 101.7, 102.3, 101.4, 102.1, 9),
        };
        client.RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>())
              .Returns(freshBars);

        var selector = new SingleClientSelector(client);
        var connection = new ConnectionManager(selector, NullLogger<ConnectionManager>.Instance);

        var repo = new MarketDataRepository(
            selector, connection, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        var bars = await repo.GetHistoricalBarsAsync(contract, size, duration);

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

        // Three bars from yesterday — count matches but freshness window is blown.
        var stale = DateTime.UtcNow.AddDays(-1);
        var cached = new[]
        {
            new OhlcvBar(instrumentId, size, stale.AddMinutes(-3), 100, 101, 99.5, 100.5, 10, BrokerKind.InteractiveBrokers, true),
            new OhlcvBar(instrumentId, size, stale.AddMinutes(-2), 100.5, 102, 100.2, 101.7, 12, BrokerKind.InteractiveBrokers, true),
            new OhlcvBar(instrumentId, size, stale.AddMinutes(-1), 101.7, 102.3, 101.4, 102.1, 9, BrokerKind.InteractiveBrokers, true),
        };

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(contract).Returns(instrumentId);
        var hub = new MarketDataHub();
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(instrumentId, size, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(cached);

        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.InteractiveBrokers);
        client.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Connected));
        var freshBars = new[]
        {
            new Bar(DateTime.UtcNow.AddMinutes(-3), 200, 201, 199.5, 200.5, 10),
            new Bar(DateTime.UtcNow.AddMinutes(-2), 200.5, 202, 200.2, 201.7, 12),
            new Bar(DateTime.UtcNow.AddMinutes(-1), 201.7, 202.3, 201.4, 202.1, 9),
        };
        client.RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>())
              .Returns(freshBars);

        var selector = new SingleClientSelector(client);
        var connection = new ConnectionManager(selector, NullLogger<ConnectionManager>.Instance);

        var repo = new MarketDataRepository(
            selector, connection, new ImmediateDispatcher(),
            ingest, hub, store,
            NullLogger<MarketDataRepository>.Instance);

        var bars = await repo.GetHistoricalBarsAsync(contract, size, duration);

        bars[0].Open.Should().Be(200, because: "stale cache must be bypassed and fresh broker data returned");
        await client.Received(1).RequestHistoricalBarsAsync(contract, size, duration, Arg.Any<CancellationToken>());
    }

    private sealed class SingleClientSelector : IBrokerSelector
    {
        public SingleClientSelector(IBrokerClient client)
        {
            client.Kind.Returns(BrokerKind.InteractiveBrokers);
            client.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected));
            Active = client;
            ActiveMode = new BrokerConnectionMode(client.Kind, false, "Test", "Test");
        }

        public BrokerKind ActiveKind => Active.Kind;
        public IBrokerClient Active { get; }
        public BrokerConnectionMode ActiveMode { get; }
        public IReadOnlyList<BrokerKind> AvailableKinds => new[] { Active.Kind };
        public bool IsAvailable(BrokerKind kind) => kind == Active.Kind;
        public event EventHandler? ActiveChanged { add { } remove { } }
        public void SetActive(BrokerKind kind) { /* test selector — single client */ }
    }
}
