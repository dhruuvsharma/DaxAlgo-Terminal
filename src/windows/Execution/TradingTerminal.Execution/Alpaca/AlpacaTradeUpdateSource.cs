using System.Threading.Channels;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Alpaca;

/// <summary>Injected asynchronous order-update source; production uses bounded REST polling.</summary>
public interface IAlpacaTradeUpdateSource : IAsyncDisposable
{
    bool IsRunning { get; }

    event Action<AlpacaOrderSnapshot>? OrderUpdated;

    event Action<Exception>? Faulted;

    Task StartAsync(IAlpacaExecutionTransport transport, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Bounded single-consumer scheduler for adapter callbacks.</summary>
public sealed class AlpacaSerializedEventScheduler : IAdapterEventScheduler, IDisposable, IAsyncDisposable
{
    private const int CallbackCapacity = 4_096;
    private readonly Channel<Action> _callbacks = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(CallbackCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Task _consumer;
    private readonly AsyncLocal<bool> _insideConsumer = new();
    private volatile bool _completed;

    public AlpacaSerializedEventScheduler() => _consumer = Task.Run(ConsumeAsync);

    public Exception? LastCallbackFault { get; private set; }

    public event Action<Exception>? CallbackFaulted;

    public void Schedule(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_callbacks.Writer.TryWrite(callback))
            return;
        if (_completed)
            throw new ObjectDisposedException(nameof(AlpacaSerializedEventScheduler));
        var fault = new InvalidOperationException("Alpaca execution-event queue overflow.");
        LastCallbackFault = fault;
        CallbackFaulted?.Invoke(fault);
        throw fault;
    }

    public void Dispose()
    {
        _completed = true;
        _callbacks.Writer.TryComplete();
        if (!_insideConsumer.Value)
            _consumer.GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _completed = true;
        _callbacks.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var callback in _callbacks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _insideConsumer.Value = true;
                callback();
            }
            catch (Exception exception)
            {
                LastCallbackFault = exception;
                CallbackFaulted?.Invoke(exception);
            }
            finally
            {
                _insideConsumer.Value = false;
            }
        }
    }
}

/// <summary>
/// Production trade-update mechanism. It polls GET /v2/orders?status=all and emits only changed
/// bounded fingerprints; tests inject a deterministic source and create no timer or socket.
/// </summary>
public sealed class AlpacaPollingTradeUpdateSource : IAlpacaTradeUpdateSource, IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly int _maximumTrackedOrders;
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private CancellationTokenSource? _stopSource;
    private Task? _pollTask;
    private bool _disposed;
    private bool _incompletePageReported;

    public AlpacaPollingTradeUpdateSource(TimeSpan interval, int maximumTrackedOrders)
    {
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (maximumTrackedOrders is < 32 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedOrders));
        _interval = interval;
        _maximumTrackedOrders = maximumTrackedOrders;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _pollTask is { IsCompleted: false };
        }
    }

    public event Action<AlpacaOrderSnapshot>? OrderUpdated;

    public event Action<Exception>? Faulted;

    public Task StartAsync(IAlpacaExecutionTransport transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pollTask is { IsCompleted: false })
                return Task.CompletedTask;
            _stopSource?.Dispose();
            _stopSource = new CancellationTokenSource();
            _incompletePageReported = false;
            _pollTask = Task.Run(() => PollAsync(transport, _stopSource.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_gate)
        {
            _stopSource?.Cancel();
            task = _pollTask;
        }
        if (task is null)
            return;
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        lock (_gate)
        {
            if (ReferenceEquals(task, _pollTask))
                _pollTask = null;
        }
    }

    public void Dispose()
    {
        Task? pollTask;
        CancellationTokenSource? stopSource;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopSource?.Cancel();
            pollTask = _pollTask;
            stopSource = _stopSource;
        }
        if (pollTask is not null)
        {
            try
            {
                pollTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        lock (_gate)
        {
            if (ReferenceEquals(_pollTask, pollTask))
                _pollTask = null;
            if (ReferenceEquals(_stopSource, stopSource))
                _stopSource = null;
            _fingerprints.Clear();
            _insertionOrder.Clear();
        }
        stopSource?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _disposed = true;
            _stopSource?.Dispose();
            _stopSource = null;
            _fingerprints.Clear();
            _insertionOrder.Clear();
        }
    }

    private async Task PollAsync(IAlpacaExecutionTransport transport, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<AlpacaOrderSnapshot> orders;
                try
                {
                    orders = await transport.GetOrdersAsync(AlpacaOrderStatusFilter.All, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    ReportFault(exception);
                    continue;
                }

                foreach (var order in orders.Take(_maximumTrackedOrders))
                {
                    if (!RememberChange(order))
                        continue;
                    try
                    {
                        OrderUpdated?.Invoke(order);
                    }
                    catch (Exception exception)
                    {
                        ReportFault(exception);
                    }
                }

                if (orders.Count >= 500)
                {
                    if (!_incompletePageReported)
                    {
                        _incompletePageReported = true;
                        ReportFault(new InvalidDataException(
                            "The bounded Alpaca all-orders poll reached its 500-order limit; recent rows were processed but execution admission must remain fail-closed until reconciliation."));
                    }
                }
                else
                {
                    _incompletePageReported = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool RememberChange(AlpacaOrderSnapshot order)
    {
        var fingerprint = string.Concat(
            order.Status, "|",
            order.FilledQuantity.Coefficient, ":", order.FilledQuantity.Scale, "|",
            order.FilledAveragePrice?.Coefficient, ":", order.FilledAveragePrice?.Scale, "|",
            order.UpdatedAtUtc.Ticks);
        lock (_gate)
        {
            if (_fingerprints.TryGetValue(order.OrderId, out var prior))
            {
                if (string.Equals(prior, fingerprint, StringComparison.Ordinal))
                    return false;
                _fingerprints[order.OrderId] = fingerprint;
                return true;
            }
            while (_fingerprints.Count >= _maximumTrackedOrders && _insertionOrder.TryDequeue(out var oldest))
                _fingerprints.Remove(oldest);
            _fingerprints.Add(order.OrderId, fingerprint);
            _insertionOrder.Enqueue(order.OrderId);
            return true;
        }
    }

    private void ReportFault(Exception exception)
    {
        try
        {
            Faulted?.Invoke(exception);
        }
        catch
        {
            // A diagnostic subscriber cannot terminate the one bounded polling loop.
        }
    }
}
