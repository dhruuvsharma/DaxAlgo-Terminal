using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;

namespace DaxAlgo.Sandbox.Samples.Tests;

/// <summary>A minimal public strategy context for open-core kernel unit tests.</summary>
public sealed class TestStrategyRuntimeContext(
    IMarketDataView data,
    IClock clock,
    IParameters parameters,
    IVirtualBook book,
    IAlertSink alerts) : IStrategyRuntimeContext
{
    public IMarketDataView Data { get; } = data ?? throw new ArgumentNullException(nameof(data));

    public IClock Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));

    public IParameters Parameters { get; } = parameters ?? throw new ArgumentNullException(nameof(parameters));

    public IVirtualBook Book { get; } = book ?? throw new ArgumentNullException(nameof(book));

    public IAlertSink Alerts { get; } = alerts ?? throw new ArgumentNullException(nameof(alerts));
}

/// <summary>Records declarative model targets without providing any account or execution access.</summary>
public sealed class RecordingVirtualBook : IVirtualBook
{
    private readonly List<VirtualTargetIntent> _intents = [];

    public IReadOnlyList<VirtualTargetIntent> Intents => _intents;

    public void SubmitTarget(VirtualTargetIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        _intents.Add(intent);
    }
}

/// <summary>A mutable deterministic clock used by the sample hosts.</summary>
public sealed class TestClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

/// <summary>A tiny in-memory implementation of the public market-data hub for sample tests.</summary>
public sealed class InMemoryMarketDataHub : IMarketDataHub
{
    private readonly Dictionary<InstrumentId, TestObservable<Quote>> _quotes = [];
    private readonly Dictionary<InstrumentId, TestObservable<TradePrint>> _trades = [];
    private readonly Dictionary<(InstrumentId Instrument, BarSize Size), TestObservable<OhlcvBar>> _bars = [];
    private readonly Dictionary<InstrumentId, TestObservable<DepthSnapshot>> _depth = [];

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

    private static TestObservable<TValue> Stream<TKey, TValue>(
        Dictionary<TKey, TestObservable<TValue>> streams,
        TKey key)
        where TKey : notnull
    {
        if (!streams.TryGetValue(key, out var stream))
        {
            stream = new TestObservable<TValue>();
            streams.Add(key, stream);
        }

        return stream;
    }

    private sealed class TestObservable<T> : IObservable<T>
    {
        private readonly object _gate = new();
        private readonly List<IObserver<T>> _observers = [];

        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_gate)
                _observers.Add(observer);
            return new Subscription(this, observer);
        }

        public void Publish(T value)
        {
            IObserver<T>[] observers;
            lock (_gate)
                observers = [.. _observers];

            foreach (var observer in observers)
                observer.OnNext(value);
        }

        private void Unsubscribe(IObserver<T> observer)
        {
            lock (_gate)
                _observers.Remove(observer);
        }

        private sealed class Subscription(TestObservable<T> owner, IObserver<T> observer) : IDisposable
        {
            private TestObservable<T>? _owner = owner;

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref _owner, null);
                current?.Unsubscribe(observer);
            }
        }
    }
}
