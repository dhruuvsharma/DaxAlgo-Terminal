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

/// <summary>
/// Captures what a <c>Draw</c> call produced, so drawing can be tested the same way any other output
/// is — by asserting on it.
///
/// <para>This is how you check your own visualizer. <c>Draw</c> takes an interface, not a control, so
/// nothing here needs a window, a dispatcher or a running host: construct one of these, call
/// <c>Draw</c>, and read what came back. A visualizer that compiles and paints nothing is the easiest
/// mistake to ship and the hardest to notice, because the panel simply looks empty.</para>
/// </summary>
public sealed class RecordingRenderSurface(double width = 800d, double height = 400d) : IRenderSurface
{
    private readonly List<string> _calls = [];

    /// <summary>Every primitive call, in order, as `name` or `name:detail`.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Panel titles in the order they were opened.</summary>
    public IReadOnlyList<string> Panels { get; } = new List<string>();

    /// <summary>Series names in the order they were opened.</summary>
    public List<string> Series_ { get; } = [];

    /// <summary>Every point pushed into any series.</summary>
    public List<(double X, double Y)> Points { get; } = [];

    /// <summary>Text drawn, in order.</summary>
    public List<string> Texts { get; } = [];

    /// <summary>Markers drawn, in order.</summary>
    public List<(double X, double Y, RenderMarkerShape Shape)> Markers { get; } = [];

    public RenderViewport Viewport { get; } = new(width, height, 1d);

    public RenderCursor Cursor { get; init; } = new(0d, 0d, false, false);

    public RenderColor Theme(RenderThemeColor token)
    {
        _calls.Add($"Theme:{token}");
        return new RenderColor(128, 128, 128);
    }

    public void SetStyle(RenderStyle style) => _calls.Add("SetStyle");

    public IDisposable Panel(string title, RenderPanelKind kind)
    {
        _calls.Add($"Panel:{title}");
        ((List<string>)Panels).Add(title);
        return new Scope();
    }

    public void AxisX(double minimum, double maximum, string? format = null) =>
        _calls.Add($"AxisX:{minimum}:{maximum}");

    public void AxisY(double minimum, double maximum, string? format = null) =>
        _calls.Add($"AxisY:{minimum}:{maximum}");

    public IDisposable Series(string name, RenderSeriesKind kind)
    {
        _calls.Add($"Series:{name}");
        Series_.Add(name);
        return new Scope();
    }

    public void Push(double x, double y)
    {
        _calls.Add("Push");
        Points.Add((x, y));
    }

    public void Line(double x1, double y1, double x2, double y2) => _calls.Add("Line");

    public void Rect(double x, double y, double width, double height, bool filled = true) =>
        _calls.Add("Rect");

    public void Text(double x, double y, string text)
    {
        _calls.Add($"Text:{text}");
        Texts.Add(text);
    }

    public void Marker(double x, double y, RenderMarkerShape shape)
    {
        _calls.Add($"Marker:{shape}");
        Markers.Add((x, y, shape));
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() { }
    }
}
