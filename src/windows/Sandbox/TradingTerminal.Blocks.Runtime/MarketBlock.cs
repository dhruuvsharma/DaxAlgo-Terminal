using DaxAlgo.Blocks;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>
/// Live market data for any number of instruments, subscribed while the unit runs.
///
/// <para>The sandbox runtimes subscribe once, at start, to the instruments a unit declared. A unit
/// built from blocks decides as it goes — an arbitrage adds its second leg, a screener adds whatever
/// the page asks for — so each stream is subscribed on its first handler and released after its last.</para>
///
/// <para>The recent window fills on the hub's thread as data arrives, so a handler reading
/// <c>RecentBars</c> sees the bar it is being called for, and it lives exactly as long as the stream has
/// a handler. Handlers themselves only ever run on the unit thread.</para>
/// </summary>
internal sealed class MarketBlock(
    IMarketDataHub hub,
    UnitThread thread,
    int window,
    Action<Exception> fault,
    Func<InstrumentId, MarketFeed, BarSize, IDisposable?>? openFeed = null,
    Action<string>? warn = null)
    : IMarketData, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<StreamKey, Stream> _streams = new();
    private bool _disposed;

    public IDisposable OnQuote(InstrumentId instrument, Action<Quote> handler) =>
        Subscribe(new StreamKey(MarketEventKind.Quote, instrument, default), handler);

    public IDisposable OnTrade(InstrumentId instrument, Action<TradePrint> handler) =>
        Subscribe(new StreamKey(MarketEventKind.Trade, instrument, default), handler);

    public IDisposable OnBar(InstrumentId instrument, BarSize size, Action<OhlcvBar> handler) =>
        Subscribe(new StreamKey(MarketEventKind.Bar, instrument, size), handler);

    public IDisposable OnDepth(InstrumentId instrument, Action<DepthSnapshot> handler) =>
        Subscribe(new StreamKey(MarketEventKind.Depth, instrument, default), handler);

    public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int count) =>
        Recent<OhlcvBar>(new StreamKey(MarketEventKind.Bar, instrument, size), count);

    public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int count) =>
        Recent<Quote>(new StreamKey(MarketEventKind.Quote, instrument, default), count);

    public IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int count) =>
        Recent<TradePrint>(new StreamKey(MarketEventKind.Trade, instrument, default), count);

    public DepthSnapshot? LatestDepth(InstrumentId instrument) =>
        Recent<DepthSnapshot>(new StreamKey(MarketEventKind.Depth, instrument, default), 1) is [var latest] ? latest : null;

    /// <summary>The instruments the unit currently has any stream open for.</summary>
    public IReadOnlyCollection<InstrumentId> Instruments
    {
        get
        {
            lock (_gate)
                return [.. _streams.Where(s => s.Value.Handlers.Count > 0).Select(s => s.Key.Instrument).Distinct()];
        }
    }

    /// <summary>Calls every handler registered for the event's stream. Unit thread only.</summary>
    public void Dispatch(MarketEvent evt)
    {
        Delegate[] handlers;
        lock (_gate)
        {
            if (!_streams.TryGetValue(new StreamKey(evt.Kind, evt.Instrument, evt.Size), out var stream)) return;
            handlers = [.. stream.Handlers];
        }

        foreach (var handler in handlers)
        {
            // One failing handler must not starve the others registered on the same stream.
            try
            {
                switch (evt.Kind)
                {
                    case MarketEventKind.Quote: ((Action<Quote>)handler)((Quote)evt.Payload); break;
                    case MarketEventKind.Trade: ((Action<TradePrint>)handler)((TradePrint)evt.Payload); break;
                    case MarketEventKind.Bar: ((Action<OhlcvBar>)handler)((OhlcvBar)evt.Payload); break;
                    case MarketEventKind.Depth: ((Action<DepthSnapshot>)handler)((DepthSnapshot)evt.Payload); break;
                }
            }
            catch (Exception ex)
            {
                fault(ex);
            }
        }
    }

    private IDisposable Subscribe(StreamKey key, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (key.Instrument.IsNone)
            throw new ArgumentException("Choose an instrument before subscribing; this one is unset.", nameof(key));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_streams.TryGetValue(key, out var stream))
                _streams[key] = stream = new Stream(window);

            stream.Handlers.Add(handler);
            stream.Subscription ??= Open(key, stream);
        }

        return new Disposer(() =>
        {
            lock (_gate)
            {
                if (!_streams.TryGetValue(key, out var stream)) return;
                stream.Handlers.Remove(handler);
                if (stream.Handlers.Count > 0) return;

                // The last handler has gone: stop the feed and drop the window with it. Keeping windows for
                // streams nobody listens to would let a unit that cycles through instruments pin 512
                // items per stream it ever touched.
                stream.Subscription?.Dispose();
                stream.Subscription = null;
                _streams.Remove(key);
            }
        });
    }

    private IDisposable Open(StreamKey key, Stream stream)
    {
        void Arrive(InstrumentId instrument, object payload)
        {
            if (instrument != key.Instrument) return;
            stream.Recent.Add(payload);
            thread.Enqueue(new MarketEvent(key.Kind, key.Instrument, key.Size, payload));
        }

        var listening = key.Kind switch
        {
            MarketEventKind.Quote => hub.Quotes(key.Instrument).Subscribe(new Observer<Quote>(q => Arrive(q.InstrumentId, q))),
            MarketEventKind.Trade => hub.Trades(key.Instrument).Subscribe(new Observer<TradePrint>(t => Arrive(t.InstrumentId, t))),
            MarketEventKind.Bar => hub.Bars(key.Instrument, key.Size).Subscribe(new Observer<OhlcvBar>(b =>
            {
                if (b.Size == key.Size) Arrive(b.InstrumentId, b);
            })),
            MarketEventKind.Depth => hub.Depth(key.Instrument).Subscribe(new Observer<DepthSnapshot>(d => Arrive(key.Instrument, d))),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        return new Both(listening, StartFeed(key));
    }

    /// <summary>Asks the host to start the venue stream behind a subscription. A refusal is said and
    /// survived: the hub may still be fed by another window or a recording.</summary>
    private IDisposable? StartFeed(StreamKey key)
    {
        if (openFeed is null) return null;

        var feed = key.Kind switch
        {
            MarketEventKind.Quote => MarketFeed.Quotes,
            MarketEventKind.Trade => MarketFeed.Trades,
            MarketEventKind.Bar => MarketFeed.Bars,
            _ => MarketFeed.Depth,
        };

        try
        {
            return openFeed(key.Instrument, feed, key.Size);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Could not start the {feed} feed for {key.Instrument}: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<T> Recent<T>(StreamKey key, int count)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(key, out var stream)) return [];
            return [.. stream.Recent.Newest(count).Cast<T>()];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stream in _streams.Values)
            {
                try { stream.Subscription?.Dispose(); } catch { /* a hub failing to unsubscribe must not stop the rest */ }
                stream.Subscription = null;
                stream.Handlers.Clear();
            }
        }
    }

    private sealed class Both(IDisposable listening, IDisposable? feed) : IDisposable
    {
        public void Dispose()
        {
            // The hub first, so nothing arrives for a stream whose feed is already going away.
            listening.Dispose();
            try { feed?.Dispose(); } catch { /* a venue failing to stop must not stop the rest */ }
        }
    }

    private readonly record struct StreamKey(MarketEventKind Kind, InstrumentId Instrument, BarSize Size);

    private sealed class Stream(int window)
    {
        public RingBuffer<object> Recent { get; } = new(window);
        public List<Delegate> Handlers { get; } = [];
        public IDisposable? Subscription { get; set; }
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => next(value);
    }
}
