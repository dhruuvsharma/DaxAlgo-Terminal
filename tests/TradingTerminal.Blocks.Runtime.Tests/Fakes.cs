using DaxAlgo.Blocks;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Blocks.Runtime.Tests;

internal sealed class FakeHub : IMarketDataHub
{
    private readonly Subject<Quote> _quotes = new();
    private readonly Subject<TradePrint> _trades = new();
    private readonly Subject<OhlcvBar> _bars = new();
    private readonly Subject<(InstrumentId, DepthSnapshot)> _depth = new();

    public int Subscribers => _quotes.Count + _trades.Count + _bars.Count + _depth.Count;

    public IObservable<Quote> Quotes(InstrumentId instrumentId) => _quotes;
    public IObservable<TradePrint> Trades(InstrumentId instrumentId) => _trades;
    public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) => _bars;
    public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) => new DepthFor(_depth, instrumentId);

    public void PublishQuote(Quote quote) => _quotes.OnNext(quote);
    public void PublishTrade(TradePrint trade) => _trades.OnNext(trade);
    public void PublishBar(OhlcvBar bar) => _bars.OnNext(bar);
    public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) => _depth.OnNext((instrumentId, snapshot));

    public static Quote Quote(int instrument, double bid, double ask) => new(
        new InstrumentId(instrument), DateTime.UtcNow, DateTime.UtcNow, bid, ask, 10, 10,
        BrokerKind.Simulated, 0, false);

    private sealed class DepthFor(Subject<(InstrumentId, DepthSnapshot)> source, InstrumentId id) : IObservable<DepthSnapshot>
    {
        public IDisposable Subscribe(IObserver<DepthSnapshot> observer) =>
            source.Subscribe(new Relay(observer, id));

        private sealed class Relay(IObserver<DepthSnapshot> inner, InstrumentId id) : IObserver<(InstrumentId, DepthSnapshot)>
        {
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext((InstrumentId, DepthSnapshot) value) { if (value.Item1 == id) inner.OnNext(value.Item2); }
        }
    }
}

internal sealed class Subject<T> : IObservable<T>
{
    private readonly object _gate = new();
    private readonly List<IObserver<T>> _observers = [];

    public int Count { get { lock (_gate) return _observers.Count; } }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_gate) _observers.Add(observer);
        return new Unsubscribe(() => { lock (_gate) _observers.Remove(observer); });
    }

    public void OnNext(T value)
    {
        IObserver<T>[] observers;
        lock (_gate) observers = [.. _observers];
        foreach (var observer in observers) observer.OnNext(value);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
}

internal sealed class FakeEndpoint : IUnitUiEndpoint
{
    private readonly object _gate = new();
    public List<(string Topic, string Json)> Posts { get; } = [];
    public bool IsOpen { get; set; }

    public event Action<string, string>? MessageReceived;
    public event Action? Opened;

    public void Post(string topic, string json)
    {
        lock (_gate) Posts.Add((topic, json));
    }

    public (string Topic, string Json)[] Snapshot() { lock (_gate) return [.. Posts]; }

    public void Open()
    {
        IsOpen = true;
        Opened?.Invoke();
    }

    public void Send(string topic, string json) => MessageReceived?.Invoke(topic, json);
}

internal sealed class MemoryStateStore : IUnitStateStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _saved = new();

    public IReadOnlyDictionary<string, string>? Load(string unitId) => _saved.GetValueOrDefault(unitId);

    public void Save(string unitId, IReadOnlyDictionary<string, string> values) => _saved[unitId] = values;
}

internal static class Host
{
    public static BlocksHost For(FakeHub hub, List<string>? log = null, IUnitStateStore? state = null) => new(
        hub,
        new FakeClock(),
        (source, level, message) => { if (log is not null) lock (log) log.Add($"{level} {message}"); },
        _ => { },
        StateStore: state);

    public static async Task WaitUntil(Func<bool> condition, string because, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting: {because}");
            await Task.Delay(10);
        }
    }
}

/// <summary>A unit written the way a generated one would be, from its StartAsync.</summary>
internal sealed class LambdaUnit(UnitInfo info, Func<IUnitContext, Task> start, Func<Task>? stop = null) : IUnit
{
    public UnitInfo Info { get; } = info;
    public Task StartAsync(IUnitContext context, CancellationToken ct) => start(context);
    public Task StopAsync(CancellationToken ct) => stop?.Invoke() ?? Task.CompletedTask;
}
