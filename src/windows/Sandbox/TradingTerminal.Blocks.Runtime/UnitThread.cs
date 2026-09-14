using System.Threading.Channels;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>What kind of market event is waiting.</summary>
internal enum MarketEventKind
{
    Quote,
    Trade,
    Bar,
    Depth,
}

/// <summary>One market event on its way to the unit thread.</summary>
internal readonly record struct MarketEvent(MarketEventKind Kind, InstrumentId Instrument, BarSize Size, object Payload);

/// <summary>
/// The unit's own thread: every handler a unit registers runs here, one at a time, so a unit never
/// needs a lock.
///
/// <para><b>Two queues, because they are not worth the same.</b> Market events are frequent and only the
/// newest ones matter, so their queue drops the oldest when a unit falls behind. Posted work — a timer
/// tick, a message from the page, a network result handed back — is rarer and each item matters, so it
/// always goes first and is never dropped silently.</para>
///
/// <para><b>Both queues have a ceiling.</b> Posted work is not unbounded either: a page flooding
/// messages, or a unit posting from a fast network loop, would otherwise grow it until the process dies
/// — the shape of the Volume Footprint window's 20 GB. Past the ceiling a post is refused rather than
/// blocking the caller (a timer, the hub, the WebView's UI thread), counted, and reported; a lifecycle
/// call that does not fit fails its task instead of hanging on it.</para>
///
/// <para><b>One wake-up token, not one waiter per wait.</b> Waiting on both queues with
/// <c>Task.WhenAny</c> leaves a fresh waiter parked on the quiet queue every time the busy one wakes
/// the loop, and during a burst of market data those waiters pile up without bound. A single-slot
/// channel that drops extra writes cannot lose a wake-up and cannot grow.</para>
/// </summary>
internal sealed class UnitThread : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _control;

    private readonly Channel<MarketEvent> _market;

    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly Action<MarketEvent> _onMarket;
    private readonly Action<Exception> _onFault;
    private readonly AsyncLocal<bool> _onThread = new();
    private readonly Action<long> _onRefused;
    private Task? _loop;
    private long _dropped;
    private long _refused;

    public UnitThread(
        int marketCapacity, Action<MarketEvent> onMarket, Action<Exception> onFault,
        int controlCapacity = 16_384, Action<long>? onRefused = null)
    {
        _onMarket = onMarket;
        _onFault = onFault;
        _onRefused = onRefused ?? (_ => { });

        // Wait mode, used only through TryWrite: a full queue refuses the write and says so, where
        // DropWrite would report success for work it threw away.
        _control = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(Math.Max(16, controlCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _market = Channel.CreateBounded<MarketEvent>(
            new BoundedChannelOptions(Math.Max(16, marketCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Market events dropped because the unit fell behind.</summary>
    public long DroppedMarketEvents => Interlocked.Read(ref _dropped);

    /// <summary>Posted work refused because the control queue was full.</summary>
    public long RefusedWork => Interlocked.Read(ref _refused);

    /// <summary>True when the caller is running on this unit thread.</summary>
    public bool IsCurrent => _onThread.Value;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Queues work for the unit thread; safe from any thread.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_stop.IsCancellationRequested) return;

        var accepted = _control.Writer.TryWrite(() =>
        {
            work();
            return Task.CompletedTask;
        });

        if (!accepted)
        {
            Refuse();
            return;
        }

        _wake.Writer.TryWrite(0);
    }

    /// <summary>Runs <paramref name="work"/> on the unit thread and completes when it has.</summary>
    public Task InvokeAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_stop.IsCancellationRequested)
        {
            done.SetCanceled();
            return done.Task;
        }

        var accepted = _control.Writer.TryWrite(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        if (!accepted)
        {
            Refuse();
            done.TrySetException(new InvalidOperationException(
                "The unit is not keeping up: its work queue is full, so this call was refused rather than queued."));
            return done.Task;
        }

        _wake.Writer.TryWrite(0);
        return done.Task;
    }

    private void Refuse()
    {
        var refused = Interlocked.Increment(ref _refused);

        // Reported on the first refusal of a burst and then every thousand, so a flood cannot turn into
        // a second flood in the activity log.
        if (refused == 1 || refused % 1000 == 0) _onRefused(refused);
    }

    /// <summary>Queues a market event; the oldest are dropped when the unit falls behind.</summary>
    public void Enqueue(MarketEvent evt)
    {
        if (_stop.IsCancellationRequested) return;
        _market.Writer.TryWrite(evt);
        _wake.Writer.TryWrite(0);
    }

    private async Task RunAsync()
    {
        _onThread.Value = true;

        while (!_stop.IsCancellationRequested)
        {
            if (_control.Reader.TryRead(out var work))
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _onFault(ex);
                }

                continue;
            }

            if (_market.Reader.TryRead(out var evt))
            {
                try
                {
                    _onMarket(evt);
                }
                catch (Exception ex)
                {
                    _onFault(ex);
                }

                continue;
            }

            try
            {
                await _wake.Reader.ReadAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _control.Writer.TryComplete();
        _market.Writer.TryComplete();

        if (_loop is not null && !IsCurrent)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}

/// <summary>A fixed-capacity ring of the newest items, safe to read from any thread.</summary>
internal sealed class RingBuffer<T>(int capacity)
{
    private readonly T[] _items = new T[Math.Max(1, capacity)];
    private readonly object _gate = new();
    private int _start;
    private int _count;

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

    /// <summary>The newest <paramref name="count"/> items, oldest first.</summary>
    public IReadOnlyList<T> Newest(int count)
    {
        if (count <= 0) return [];

        lock (_gate)
        {
            var take = Math.Min(count, _count);
            var result = new T[take];
            var first = (_start + _count - take) % _items.Length;
            for (var index = 0; index < take; index++)
                result[index] = _items[(first + index) % _items.Length];
            return result;
        }
    }
}

/// <summary>Runs an action once, on dispose.</summary>
internal sealed class Disposer(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
