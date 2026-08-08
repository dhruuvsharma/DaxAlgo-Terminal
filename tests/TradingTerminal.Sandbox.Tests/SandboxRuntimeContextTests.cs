using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

public sealed class ScopedMarketDataViewTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ScopeAndRequirementGateSubscriptionsAndReads()
    {
        var declared = new InstrumentId(101);
        var outside = new InstrumentId(202);
        var hub = new FakeMarketDataHub();
        var instruments = new HashSet<InstrumentId> { declared };

        using var view = new ScopedMarketDataView(
            instruments,
            StrategyDataRequirement.L1,
            hub,
            retentionBound: 4);
        instruments.Add(outside);

        Assert.Equal(1, hub.QuoteSubscriptionCount);
        Assert.Equal(0, hub.TradeSubscriptionCount);
        Assert.Equal(0, hub.BarSubscriptionCount);
        Assert.Equal(0, hub.DepthSubscriptionCount);

        hub.PublishQuote(QuoteFor(declared, 1));
        hub.PublishQuote(QuoteFor(outside, 2));

        Assert.Single(view.RecentQuotes(declared, 10));
        Assert.Empty(view.RecentQuotes(outside, 10));
        Assert.Empty(view.RecentBars(declared, BarSize.OneMinute, 10));
        Assert.Empty(view.RecentTrades(declared, 10));
        Assert.Null(view.LatestDepth(declared));
        Assert.DoesNotContain(outside, view.Instruments);
    }

    [Fact]
    public void RetentionKeepsNewestValuesInOldestToNewestOrder()
    {
        var instrument = new InstrumentId(303);
        var hub = new FakeMarketDataHub();
        using var view = new ScopedMarketDataView(
            new HashSet<InstrumentId> { instrument },
            StrategyDataRequirement.L1
                | StrategyDataRequirement.Bars
                | StrategyDataRequirement.Depth
                | StrategyDataRequirement.TradeTape,
            hub,
            retentionBound: 3);

        DepthSnapshot? latestDepth = null;
        for (var sequence = 1; sequence <= 5; sequence++)
        {
            hub.PublishQuote(QuoteFor(instrument, sequence));
            hub.PublishTrade(TradeFor(instrument, sequence));
            hub.PublishBar(BarFor(instrument, sequence));
            latestDepth = DepthFor(sequence);
            hub.PublishDepth(instrument, latestDepth);
        }

        Assert.Equal(new long[] { 3, 4, 5 }, view.RecentQuotes(instrument, 20).Select(x => x.Sequence));
        Assert.Equal(new long[] { 4, 5 }, view.RecentQuotes(instrument, 2).Select(x => x.Sequence));
        Assert.Equal(new long[] { 3, 4, 5 }, view.RecentTrades(instrument, 20).Select(x => x.Sequence));
        Assert.Equal(new long[] { 4, 5 }, view.RecentTrades(instrument, 2).Select(x => x.Sequence));
        Assert.Equal(new[] { 3d, 4d, 5d }, view.RecentBars(instrument, BarSize.OneMinute, 20).Select(x => x.Close));
        Assert.Equal(new[] { 4d, 5d }, view.RecentBars(instrument, BarSize.OneMinute, 2).Select(x => x.Close));
        Assert.Empty(view.RecentBars(instrument, BarSize.OneMinute, 0));
        Assert.Same(latestDepth, view.LatestDepth(instrument));
    }

    [Fact]
    public void ConcurrentPublishersRemainBounded()
    {
        var instrument = new InstrumentId(404);
        var hub = new FakeMarketDataHub();
        using var view = new ScopedMarketDataView(
            new HashSet<InstrumentId> { instrument },
            StrategyDataRequirement.L1,
            hub,
            retentionBound: 16);

        Parallel.For(0, 1_000, sequence => hub.PublishQuote(QuoteFor(instrument, sequence)));

        Assert.Equal(16, view.RecentQuotes(instrument, int.MaxValue).Count);
    }

    [Fact]
    public void DisposeUnsubscribesEveryAuthorizedStreamAndIsIdempotent()
    {
        var instrument = new InstrumentId(505);
        var hub = new FakeMarketDataHub();
        var view = new ScopedMarketDataView(
            new HashSet<InstrumentId> { instrument },
            StrategyDataRequirement.L1
                | StrategyDataRequirement.Bars
                | StrategyDataRequirement.Depth
                | StrategyDataRequirement.TradeTape,
            hub);

        Assert.Equal(9, hub.TotalSubscriptionCount);
        Assert.Equal(9, hub.ActiveSubscriptionCount);
        hub.PublishQuote(QuoteFor(instrument, 1));

        view.Dispose();
        view.Dispose();
        hub.PublishQuote(QuoteFor(instrument, 2));

        Assert.Equal(9, hub.TotalDisposalCount);
        Assert.Equal(0, hub.ActiveSubscriptionCount);
        Assert.Equal(new long[] { 1 }, view.RecentQuotes(instrument, 10).Select(quote => quote.Sequence));
    }

    [Fact]
    public void ConstructorFailureDisposesEveryEarlierSubscription()
    {
        var hub = new FailingSubscriptionHub(failOnAttempt: 3);

        Assert.Throws<InvalidOperationException>(() =>
            new ScopedMarketDataView(
                new HashSet<InstrumentId> { new(808) },
                StrategyDataRequirement.L1
                    | StrategyDataRequirement.Bars
                    | StrategyDataRequirement.Depth
                    | StrategyDataRequirement.TradeTape,
                hub));

        Assert.Equal(3, hub.SubscriptionAttemptCount);
        Assert.Equal(2, hub.DisposalCount);
    }

    private static Quote QuoteFor(InstrumentId instrument, long sequence) =>
        new(
            instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            sequence,
            sequence + 1,
            10,
            11,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static TradePrint TradeFor(InstrumentId instrument, long sequence) =>
        new(
            instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            sequence,
            10,
            AggressorSide.Buy,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static OhlcvBar BarFor(InstrumentId instrument, int sequence) =>
        new(
            instrument,
            BarSize.OneMinute,
            Epoch.AddMinutes(sequence),
            sequence,
            sequence,
            sequence,
            sequence,
            sequence,
            BrokerKind.Simulated,
            IsFinal: true);

    private static DepthSnapshot DepthFor(int sequence) =>
        new(
            Epoch.AddSeconds(sequence),
            new[] { new DepthLevel(sequence, sequence) },
            new[] { new DepthLevel(sequence + 1, sequence) });
}

public sealed class MediatedAlertSinkTests
{
    [Fact]
    public void RejectsOverLengthMessageAndDedupeKey()
    {
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var sink = new MediatedAlertSink("Test strategy", clock, (_, _, _) => { }, _ => { });

        Assert.Throws<ArgumentException>(() =>
            sink.Alert(new string('m', AlertLimits.MaxMessageLength + 1), AlertLevel.Information));
        Assert.Throws<ArgumentException>(() =>
            sink.Alert(
                "message",
                AlertLevel.Information,
                new string('k', AlertLimits.MaxDedupeKeyLength + 1)));
    }

    [Fact]
    public void DedupeThrottleAndFixedRoutesAreAppliedWithinHostWindow()
    {
        var start = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var clock = new MutableClock(start);
        var logs = new List<(string Source, string Level, string Message)>();
        var banners = new List<AlertRecord>();
        var sink = new MediatedAlertSink(
            "Mean Reversion",
            clock,
            (source, level, message) => logs.Add((source, level, message)),
            banners.Add,
            window: TimeSpan.FromMinutes(1),
            maxAlertsPerWindow: 2);

        sink.Alert("first", AlertLevel.Warning, "position");
        sink.Alert("duplicate", AlertLevel.Critical, "position");
        sink.AlertIf(condition: false, "conditional", AlertLevel.Error);
        sink.Alert("second", AlertLevel.Information);
        sink.Alert("throttled", AlertLevel.Error, "other");

        Assert.Equal(2, logs.Count);
        Assert.Equal(2, banners.Count);
        Assert.Equal(("Mean Reversion", "WARN", "first"), logs[0]);
        Assert.Equal("second", banners[1].Message);

        clock.UtcNow = start.AddMinutes(1);
        sink.Alert("after window", AlertLevel.Critical, "position");

        Assert.Equal(3, logs.Count);
        Assert.Equal(("Mean Reversion", "CRITICAL", "after window"), logs[2]);
        Assert.Equal(3, banners.Count);
    }
}

public sealed class SandboxParametersTests
{
    private enum TestMode
    {
        Fast,
        Slow,
    }

    [Fact]
    public void TypedReadsReturnCurrentValuesAndRejectUnknownOrWrongKinds()
    {
        var instrument = new InstrumentId(606);
        var schema = new StrategyParameterSchema(
            StrategyParameter.Int("whole", "Whole", 1),
            new StrategyParameter
            {
                Key = "wide",
                DisplayName = "Wide",
                Kind = ParameterKind.Integer,
                Default = (long)int.MaxValue + 1,
            },
            StrategyParameter.Number("real", "Real", 1.5),
            StrategyParameter.Bool("enabled", "Enabled", false),
            StrategyParameter.Enum("mode", "Mode", TestMode.Fast),
            StrategyParameter.Text("notes", "Notes"),
            StrategyParameter.Instrument("instrument", "Instrument", InstrumentId.None));
        var values = new Dictionary<string, object?>
        {
            ["whole"] = 7,
            ["wide"] = (long)int.MaxValue + 1,
            ["real"] = 2.5,
            ["enabled"] = true,
            ["mode"] = nameof(TestMode.Slow),
            ["notes"] = "ready",
            ["instrument"] = instrument,
        };
        var parameters = new SandboxParameters(schema, values);

        Assert.Same(schema, parameters.Schema);
        Assert.Equal(7, parameters.GetInt("whole"));
        Assert.Equal(7L, parameters.GetLong("whole"));
        Assert.Equal((long)int.MaxValue + 1, parameters.GetLong("wide"));
        Assert.Equal(2.5, parameters.GetDouble("real"));
        Assert.True(parameters.GetBool("enabled"));
        Assert.Equal(nameof(TestMode.Slow), parameters.GetString("mode"));
        Assert.Equal(TestMode.Slow, parameters.GetEnum<TestMode>("mode"));
        Assert.Equal("ready", parameters.GetString("notes"));
        Assert.Equal("ready", parameters.GetText("notes"));
        Assert.Equal(instrument, parameters.GetInstrument("instrument"));

        Assert.Throws<KeyNotFoundException>(() => parameters.GetBool("missing"));
        Assert.Throws<OverflowException>(() => parameters.GetInt("wide"));
        Assert.Throws<InvalidOperationException>(() => parameters.GetDouble("whole"));
        Assert.Throws<InvalidOperationException>(() => parameters.GetText("mode"));
    }
}

public sealed class SandboxVisualizerContextTests
{
    [Fact]
    public void FactoryBuildsCompleteVisualizerContextAndOwnsDataSubscription()
    {
        var instrument = new InstrumentId(707);
        var hub = new FakeMarketDataHub();
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var context = SandboxVisualizerContextFactory.Create(
            new HashSet<InstrumentId> { instrument },
            StrategyDataRequirement.L1,
            hub,
            clock,
            StrategyParameterSchema.Empty,
            currentValues: null,
            source: "Visualizer",
            appendActivityLog: (_, _, _) => { },
            showBanner: _ => { });

        Assert.IsType<ScopedMarketDataView>(context.Data);
        Assert.Same(clock, context.Clock);
        Assert.IsType<SandboxParameters>(context.Parameters);
        Assert.IsType<MediatedAlertSink>(context.Alerts);
        Assert.Equal(1, hub.ActiveSubscriptionCount);

        context.Dispose();

        Assert.Equal(0, hub.ActiveSubscriptionCount);
        Assert.Equal(1, hub.TotalDisposalCount);
    }
}

internal sealed class MutableClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

internal sealed class FakeMarketDataHub : IMarketDataHub
{
    private readonly Dictionary<InstrumentId, TrackingObservable<Quote>> _quotes = new();
    private readonly Dictionary<InstrumentId, TrackingObservable<TradePrint>> _trades = new();
    private readonly Dictionary<(InstrumentId Instrument, BarSize Size), TrackingObservable<OhlcvBar>> _bars = new();
    private readonly Dictionary<InstrumentId, TrackingObservable<DepthSnapshot>> _depth = new();

    public int QuoteSubscriptionCount => _quotes.Values.Sum(stream => stream.SubscriptionCount);
    public int TradeSubscriptionCount => _trades.Values.Sum(stream => stream.SubscriptionCount);
    public int BarSubscriptionCount => _bars.Values.Sum(stream => stream.SubscriptionCount);
    public int DepthSubscriptionCount => _depth.Values.Sum(stream => stream.SubscriptionCount);
    public int TotalSubscriptionCount =>
        QuoteSubscriptionCount + TradeSubscriptionCount + BarSubscriptionCount + DepthSubscriptionCount;
    public int TotalDisposalCount => AllStreams.Sum(stream => stream.DisposalCount);
    public int ActiveSubscriptionCount => AllStreams.Sum(stream => stream.ActiveSubscriptionCount);

    private IEnumerable<ITrackingObservable> AllStreams =>
        _quotes.Values.Cast<ITrackingObservable>()
            .Concat(_trades.Values)
            .Concat(_bars.Values)
            .Concat(_depth.Values);

    public IObservable<Quote> Quotes(InstrumentId instrumentId) => Stream(_quotes, instrumentId);

    public IObservable<TradePrint> Trades(InstrumentId instrumentId) => Stream(_trades, instrumentId);

    public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) =>
        Stream(_bars, (instrumentId, size));

    public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) => Stream(_depth, instrumentId);

    public void PublishQuote(Quote quote) => Stream(_quotes, quote.InstrumentId).Publish(quote);

    public void PublishTrade(TradePrint trade) => Stream(_trades, trade.InstrumentId).Publish(trade);

    public void PublishBar(OhlcvBar bar) => Stream(_bars, (bar.InstrumentId, bar.Size)).Publish(bar);

    public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) =>
        Stream(_depth, instrumentId).Publish(snapshot);

    private static TrackingObservable<TValue> Stream<TKey, TValue>(
        Dictionary<TKey, TrackingObservable<TValue>> streams,
        TKey key)
        where TKey : notnull
    {
        if (!streams.TryGetValue(key, out var stream))
        {
            stream = new TrackingObservable<TValue>();
            streams.Add(key, stream);
        }

        return stream;
    }
}

internal sealed class FailingSubscriptionHub(int failOnAttempt) : IMarketDataHub
{
    private int _subscriptionAttemptCount;
    private int _disposalCount;

    public int SubscriptionAttemptCount => Volatile.Read(ref _subscriptionAttemptCount);
    public int DisposalCount => Volatile.Read(ref _disposalCount);

    public IObservable<Quote> Quotes(InstrumentId instrumentId) => new FailingObservable<Quote>(this);

    public IObservable<TradePrint> Trades(InstrumentId instrumentId) => new FailingObservable<TradePrint>(this);

    public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) =>
        new FailingObservable<OhlcvBar>(this);

    public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) =>
        new FailingObservable<DepthSnapshot>(this);

    public void PublishQuote(Quote quote) { }

    public void PublishTrade(TradePrint trade) { }

    public void PublishBar(OhlcvBar bar) { }

    public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) { }

    private IDisposable Subscribe()
    {
        if (Interlocked.Increment(ref _subscriptionAttemptCount) == failOnAttempt)
            throw new InvalidOperationException("Synthetic subscription failure.");

        return new CountingDisposable(this);
    }

    private sealed class FailingObservable<T>(FailingSubscriptionHub owner) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => owner.Subscribe();
    }

    private sealed class CountingDisposable(FailingSubscriptionHub owner) : IDisposable
    {
        private FailingSubscriptionHub? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                Interlocked.Increment(ref current._disposalCount);
        }
    }
}

internal interface ITrackingObservable
{
    int SubscriptionCount { get; }
    int DisposalCount { get; }
    int ActiveSubscriptionCount { get; }
}

internal sealed class TrackingObservable<T> : IObservable<T>, ITrackingObservable
{
    private readonly object _gate = new();
    private readonly List<IObserver<T>> _observers = new();
    private int _subscriptionCount;
    private int _disposalCount;

    public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);
    public int DisposalCount => Volatile.Read(ref _disposalCount);
    public int ActiveSubscriptionCount => SubscriptionCount - DisposalCount;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
            _observers.Add(observer);
        Interlocked.Increment(ref _subscriptionCount);
        return new Subscription(this, observer);
    }

    public void Publish(T value)
    {
        IObserver<T>[] observers;
        lock (_gate)
            observers = _observers.ToArray();

        foreach (var observer in observers)
            observer.OnNext(value);
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            if (!_observers.Remove(observer))
                return;
        }

        Interlocked.Increment(ref _disposalCount);
    }

    private sealed class Subscription(TrackingObservable<T> owner, IObserver<T> observer) : IDisposable
    {
        private TrackingObservable<T>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(observer);
    }
}
