using System.Collections.Frozen;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Sandbox;

/// <summary>
/// A host-owned, read-only projection of <see cref="IMarketDataHub"/>. Instruments or stream
/// categories outside the declaration return empty results (or <see langword="null"/> for depth).
/// When bars are authorized, all host-defined <see cref="BarSize"/> values are subscribed because
/// <see cref="StrategyDataRequirement"/> does not declare a narrower size set.
/// </summary>
public sealed class ScopedMarketDataView : IMarketDataView, IDisposable
{
    public const int DefaultRetentionBound = 512;

    private static readonly BarSize[] AllBarSizes = Enum.GetValues<BarSize>();

    private readonly int _retentionBound;
    private readonly Dictionary<InstrumentId, BoundedRingBuffer<Quote>> _quotes = new();
    private readonly Dictionary<InstrumentId, BoundedRingBuffer<TradePrint>> _trades = new();
    private readonly Dictionary<(InstrumentId Instrument, BarSize Size), BoundedRingBuffer<OhlcvBar>> _bars = new();
    private readonly Dictionary<InstrumentId, LatestDepthSlot> _depth = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ReaderWriterLockSlim _callbackGate = new(LockRecursionPolicy.NoRecursion);
    private readonly object _disposeGate = new();
    private int _disposed;

    public ScopedMarketDataView(
        IReadOnlySet<InstrumentId> instruments,
        StrategyDataRequirement dataRequirement,
        IMarketDataHub hub,
        int retentionBound = DefaultRetentionBound)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(hub);
        if (retentionBound <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionBound), "Retention bound must be positive.");

        Instruments = instruments.ToFrozenSet();
        DataRequirement = dataRequirement;
        _retentionBound = retentionBound;

        try
        {
            SubscribeAuthorizedStreams(hub);
        }
        catch
        {
            try
            {
                Dispose();
            }
            catch
            {
                // Preserve the subscription failure after still attempting every teardown.
            }

            throw;
        }
    }

    public IReadOnlySet<InstrumentId> Instruments { get; }

    public StrategyDataRequirement DataRequirement { get; }

    public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int maxCount) =>
        IsAuthorized(instrument, StrategyDataRequirement.Bars)
        && _bars.TryGetValue((instrument, size), out var buffer)
            ? buffer.Snapshot(maxCount)
            : Array.Empty<OhlcvBar>();

    public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount) =>
        IsAuthorized(instrument, StrategyDataRequirement.L1)
        && _quotes.TryGetValue(instrument, out var buffer)
            ? buffer.Snapshot(maxCount)
            : Array.Empty<Quote>();

    public DepthSnapshot? LatestDepth(InstrumentId instrument) =>
        IsAuthorized(instrument, StrategyDataRequirement.Depth)
        && _depth.TryGetValue(instrument, out var slot)
            ? slot.Read()
            : null;

    public IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int maxCount) =>
        IsAuthorized(instrument, StrategyDataRequirement.TradeTape)
        && _trades.TryGetValue(instrument, out var buffer)
            ? buffer.Snapshot(maxCount)
            : Array.Empty<TradePrint>();

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _callbackGate.EnterWriteLock();
            try
            {
                Volatile.Write(ref _disposed, 1);
            }
            finally
            {
                _callbackGate.ExitWriteLock();
            }

            List<Exception>? failures = null;
            foreach (var subscription in _subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception ex)
                {
                    (failures ??= new List<Exception>()).Add(ex);
                }
            }

            _subscriptions.Clear();
            GC.SuppressFinalize(this);

            if (failures is not null)
                throw new AggregateException("One or more market-data subscriptions failed to dispose.", failures);
        }
    }

    private bool IsAuthorized(InstrumentId instrument, StrategyDataRequirement requirement) =>
        Instruments.Contains(instrument) && (DataRequirement & requirement) != 0;

    private void SubscribeAuthorizedStreams(IMarketDataHub hub)
    {
        foreach (var instrument in Instruments)
        {
            if ((DataRequirement & StrategyDataRequirement.L1) != 0)
            {
                var buffer = new BoundedRingBuffer<Quote>(_retentionBound);
                _quotes.Add(instrument, buffer);
                Subscribe(hub.Quotes(instrument), quote =>
                {
                    _callbackGate.EnterReadLock();
                    try
                    {
                        if (Volatile.Read(ref _disposed) == 0 && quote.InstrumentId == instrument)
                            buffer.Add(quote);
                    }
                    finally
                    {
                        _callbackGate.ExitReadLock();
                    }
                });
            }

            if ((DataRequirement & StrategyDataRequirement.TradeTape) != 0)
            {
                var buffer = new BoundedRingBuffer<TradePrint>(_retentionBound);
                _trades.Add(instrument, buffer);
                Subscribe(hub.Trades(instrument), trade =>
                {
                    _callbackGate.EnterReadLock();
                    try
                    {
                        if (Volatile.Read(ref _disposed) == 0 && trade.InstrumentId == instrument)
                            buffer.Add(trade);
                    }
                    finally
                    {
                        _callbackGate.ExitReadLock();
                    }
                });
            }

            if ((DataRequirement & StrategyDataRequirement.Bars) != 0)
            {
                foreach (var size in AllBarSizes)
                {
                    var buffer = new BoundedRingBuffer<OhlcvBar>(_retentionBound);
                    _bars.Add((instrument, size), buffer);
                    Subscribe(hub.Bars(instrument, size), bar =>
                    {
                        _callbackGate.EnterReadLock();
                        try
                        {
                            if (Volatile.Read(ref _disposed) == 0
                                && bar.InstrumentId == instrument
                                && bar.Size == size)
                            {
                                buffer.Add(bar);
                            }
                        }
                        finally
                        {
                            _callbackGate.ExitReadLock();
                        }
                    });
                }
            }

            if ((DataRequirement & StrategyDataRequirement.Depth) != 0)
            {
                var slot = new LatestDepthSlot();
                _depth.Add(instrument, slot);
                Subscribe(hub.Depth(instrument), snapshot =>
                {
                    _callbackGate.EnterReadLock();
                    try
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                            slot.Write(snapshot);
                    }
                    finally
                    {
                        _callbackGate.ExitReadLock();
                    }
                });
            }
        }
    }

    private void Subscribe<T>(IObservable<T> source, Action<T> onNext)
    {
        ArgumentNullException.ThrowIfNull(source);
        _subscriptions.Add(source.Subscribe(new CallbackObserver<T>(onNext)));
    }

    private sealed class CallbackObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => onNext(value);
    }

    private sealed class LatestDepthSlot
    {
        private DepthSnapshot? _value;

        public DepthSnapshot? Read() => Volatile.Read(ref _value);

        public void Write(DepthSnapshot value) => Volatile.Write(ref _value, value);
    }

    private sealed class BoundedRingBuffer<T>
    {
        private readonly object _gate = new();
        private readonly T[] _items;
        private int _start;
        private int _count;

        public BoundedRingBuffer(int capacity) => _items = new T[capacity];

        public void Add(T item)
        {
            lock (_gate)
            {
                if (_count < _items.Length)
                {
                    _items[(_start + _count) % _items.Length] = item;
                    _count++;
                    return;
                }

                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }

        public IReadOnlyList<T> Snapshot(int maxCount)
        {
            if (maxCount <= 0)
                return Array.Empty<T>();

            lock (_gate)
            {
                var take = Math.Min(_count, Math.Min(maxCount, _items.Length));
                if (take == 0)
                    return Array.Empty<T>();

                var result = new T[take];
                var first = (_start + _count - take) % _items.Length;
                for (var index = 0; index < take; index++)
                    result[index] = _items[(first + index) % _items.Length];

                return result;
            }
        }
    }
}
