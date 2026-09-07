using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Controls.Render;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The two halves an authored unit needs before it can show anything: which instruments it may be
/// pointed at, and whether anything is actually streaming the one it was pointed at.
///
/// <para><b>Both were reported by a user testing a generated order-book visualizer.</b> "The
/// instrument selector only shows alpaca instruments but I connected to other brokers too", and
/// "nothing is showing in the UI, not sure the visualizer is able to fetch any data". They are two
/// symptoms of the same omission: the setup panel was wired to the registry catalogue and to
/// <c>Resolve</c>, and to nothing that knows where a feed comes from or how to start one.</para>
/// </summary>
public sealed class AuthoredUnitDataPathTests
{
    private static SignalInstrument Row(string symbol, BrokerKind? broker) =>
        new(symbol, "Equity", new Contract(symbol, "STK", "SMART", "USD", "SMART"), broker);

    // ── which instruments the picker offers ─────────────────────────────────────────────────────

    [Fact]
    public void Every_connected_broker_keeps_its_own_instruments()
    {
        // The bug, stated: four brokers connected, and every row came back attributed to the first.
        var selector = new FakeSelector(BrokerKind.Alpaca, BrokerKind.Binance, BrokerKind.InteractiveBrokers);
        var ingest = new FakeIngest();

        var offered = AuthoredUnitInstruments.Resolve(
            [Row("SPY", BrokerKind.Alpaca), Row("BTCUSDT", BrokerKind.Binance), Row("ES", BrokerKind.InteractiveBrokers)],
            selector,
            ingest);

        Assert.Equal(3, offered.Count);
        Assert.Equal(BrokerKind.Alpaca, offered.Single(i => i.DisplayName == "SPY").Broker);
        Assert.Equal(BrokerKind.Binance, offered.Single(i => i.DisplayName == "BTCUSDT").Broker);
        Assert.Equal(BrokerKind.InteractiveBrokers, offered.Single(i => i.DisplayName == "ES").Broker);
    }

    [Fact]
    public void The_id_is_resolved_against_the_row_s_own_broker()
    {
        // Not cosmetic. The id IS the subscription: resolving BTCUSDT against Alpaca yields an id the
        // Binance feed will never publish under, so the unit subscribes to silence.
        var selector = new FakeSelector(BrokerKind.Alpaca, BrokerKind.Binance);
        var ingest = new FakeIngest();

        var offered = AuthoredUnitInstruments.Resolve([Row("BTCUSDT", BrokerKind.Binance)], selector, ingest);

        Assert.Equal(ingest.IdFor("BTCUSDT", BrokerKind.Binance), Assert.Single(offered).Id);
    }

    [Fact]
    public void A_row_with_no_broker_of_its_own_falls_back_to_a_connected_one()
    {
        // The registry fallback: SignalInstrumentCatalog rows are broker-agnostic by design, and this
        // branch is the only one they should ever take.
        var selector = new FakeSelector(BrokerKind.Alpaca);
        var offered = AuthoredUnitInstruments.Resolve([Row("SPY", null)], selector, new FakeIngest());

        Assert.Equal(BrokerKind.Alpaca, Assert.Single(offered).Broker);
    }

    [Fact]
    public void A_row_whose_broker_is_not_connected_falls_back_too()
    {
        var selector = new FakeSelector(BrokerKind.Alpaca);
        var offered = AuthoredUnitInstruments.Resolve([Row("BTCUSDT", BrokerKind.Binance)], selector, new FakeIngest());

        Assert.Equal(BrokerKind.Alpaca, Assert.Single(offered).Broker);
    }

    [Fact]
    public void One_unresolvable_symbol_does_not_empty_the_list()
    {
        var selector = new FakeSelector(BrokerKind.Alpaca);
        var ingest = new FakeIngest { Refuse = "BAD" };

        var offered = AuthoredUnitInstruments.Resolve(
            [Row("BAD", BrokerKind.Alpaca), Row("SPY", BrokerKind.Alpaca)], selector, ingest);

        Assert.Equal("SPY", Assert.Single(offered).DisplayName);
    }

    [Fact]
    public void Nothing_is_offered_while_no_broker_is_connected()
    {
        // An empty list is what makes the row fall back to the text editor, rather than showing a
        // dropdown that cannot be set.
        Assert.Empty(AuthoredUnitInstruments.Resolve([Row("SPY", null)], new FakeSelector(), new FakeIngest()));
    }

    [Fact]
    public void The_same_id_is_offered_once()
    {
        var selector = new FakeSelector(BrokerKind.Alpaca);
        var offered = AuthoredUnitInstruments.Resolve(
            [Row("SPY", BrokerKind.Alpaca), Row("SPY", BrokerKind.Alpaca)], selector, new FakeIngest());

        Assert.Single(offered);
    }

    // ── whether anything is streaming ───────────────────────────────────────────────────────────

    private static AuthoredUnitInstrument Picked(string symbol = "BTCUSDT", BrokerKind broker = BrokerKind.Binance) =>
        new(new InstrumentId(7), Row(symbol, broker), broker);

    [Fact]
    public void A_unit_that_wants_depth_gets_a_depth_feed_started()
    {
        // THE REPORTED BUG. The runtime subscribes to the hub; the hub carries only what a broker was
        // asked to stream, and nothing in this path had ever asked.
        var ingest = new FakeIngest();

        using var feed = AuthoredUnitFeed.Open(ingest, Picked(), StrategyDataRequirement.Depth);

        Assert.True(feed.IsLive);
        Assert.Equal(("BTCUSDT", BrokerKind.Binance), Assert.Single(ingest.Quotes));
    }

    [Fact]
    public void The_feed_is_started_on_the_broker_the_instrument_was_resolved_against()
    {
        var ingest = new FakeIngest();

        using var feed = AuthoredUnitFeed.Open(
            ingest, Picked("ES", BrokerKind.InteractiveBrokers), StrategyDataRequirement.L1);

        Assert.Equal(BrokerKind.InteractiveBrokers, Assert.Single(ingest.Quotes).Broker);
    }

    [Fact]
    public void A_tape_unit_gets_the_tape_and_a_bar_unit_gets_bars()
    {
        var ingest = new FakeIngest();

        using var feed = AuthoredUnitFeed.Open(
            ingest, Picked(), StrategyDataRequirement.TradeTape | StrategyDataRequirement.Bars);

        Assert.Single(ingest.Trades);
        Assert.Single(ingest.Bars);
        Assert.Empty(ingest.Quotes);
    }

    [Fact]
    public void Nothing_is_started_for_a_unit_that_asked_for_nothing()
    {
        var ingest = new FakeIngest();

        using var feed = AuthoredUnitFeed.Open(ingest, Picked(), default);

        Assert.False(feed.IsLive);
        Assert.Empty(ingest.Quotes);
    }

    [Fact]
    public void Nothing_is_started_when_the_parameter_names_no_instrument()
    {
        var ingest = new FakeIngest();

        using var feed = AuthoredUnitFeed.Open(ingest, null, StrategyDataRequirement.Depth);

        Assert.False(feed.IsLive);
    }

    [Fact]
    public void Closing_the_window_releases_every_stream()
    {
        // Ref-counted on the ingest side, so releasing ours must not be skipped — a window that leaks
        // its handle pins a broker subscription for the life of the app.
        var ingest = new FakeIngest();
        var feed = AuthoredUnitFeed.Open(
            ingest, Picked(), StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape);

        feed.Dispose();

        Assert.Equal(2, ingest.Released);
    }

    [Fact]
    public void A_venue_that_refuses_one_stream_still_gives_the_others()
    {
        // A book with no tape is still a book, and a window that fails to open because one stream was
        // declined is worse than one that opens with less.
        var ingest = new FakeIngest { RefuseTrades = true };

        using var feed = AuthoredUnitFeed.Open(
            ingest, Picked(), StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape);

        Assert.True(feed.IsLive);
        Assert.Single(ingest.Quotes);
    }

    [Fact]
    public void A_unit_with_two_instruments_gets_two_feeds_in_one_handle()
    {
        var ingest = new FakeIngest();

        var feed = AuthoredUnitFeed.OpenAll(
            ingest,
            [Picked("BTCUSDT", BrokerKind.Binance), Picked("ES", BrokerKind.InteractiveBrokers)],
            StrategyDataRequirement.Depth);

        Assert.Equal(2, ingest.Quotes.Count);

        feed.Dispose();
        Assert.Equal(2, ingest.Released);
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeIngest : IMarketDataIngest
    {
        private readonly Dictionary<(string, BrokerKind), InstrumentId> _ids = [];
        private int _next = 1;

        internal string? Refuse { get; init; }
        internal bool RefuseTrades { get; init; }
        internal List<(string Symbol, BrokerKind Broker)> Quotes { get; } = [];
        internal List<(string Symbol, BrokerKind Broker)> Trades { get; } = [];
        internal List<(string Symbol, BrokerKind Broker)> Bars { get; } = [];
        internal int Released { get; private set; }

        internal InstrumentId IdFor(string symbol, BrokerKind broker) => Resolve(
            new Contract(symbol, "STK", "SMART", "USD", "SMART"), broker);

        public InstrumentId Resolve(Contract contract, BrokerKind broker)
        {
            if (contract.Symbol == Refuse) throw new InvalidOperationException("no such symbol here");

            var key = (contract.Symbol, broker);
            if (!_ids.TryGetValue(key, out var id)) _ids[key] = id = new InstrumentId(_next++);
            return id;
        }

        public IDisposable Subscribe(Contract contract, BrokerKind broker)
        {
            Quotes.Add((contract.Symbol, broker));
            return new Handle(this);
        }

        public IDisposable SubscribeBars(Contract contract, BrokerKind broker, BarSize size)
        {
            Bars.Add((contract.Symbol, broker));
            return new Handle(this);
        }

        public IDisposable SubscribeTrades(Contract contract, BrokerKind broker)
        {
            if (RefuseTrades) throw new NotSupportedException("no tape on this venue");

            Trades.Add((contract.Symbol, broker));
            return new Handle(this);
        }

        private sealed class Handle(FakeIngest owner) : IDisposable
        {
            public void Dispose() => owner.Released++;
        }
    }

    private sealed class FakeSelector(params BrokerKind[] connected) : IBrokerSelector
    {
        public IReadOnlyList<BrokerKind> AvailableKinds => connected;

        public IReadOnlyList<BrokerKind> Connected => connected;

        public bool IsAvailable(BrokerKind kind) => connected.Contains(kind);

        public bool IsConnected(BrokerKind kind) => connected.Contains(kind);

        public event EventHandler<BrokerStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public IBrokerClient Get(BrokerKind kind) => throw new NotSupportedException();

        public BrokerConnectionMode ModeOf(BrokerKind kind) => throw new NotSupportedException();

        public IObservable<ConnectionState> StateOf(BrokerKind kind) => throw new NotSupportedException();

        public ConnectionState CurrentStateOf(BrokerKind kind) => throw new NotSupportedException();

        public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
    }
}
