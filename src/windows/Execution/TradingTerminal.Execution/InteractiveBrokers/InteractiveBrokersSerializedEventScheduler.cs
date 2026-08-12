using System.Threading.Channels;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.InteractiveBrokers;

/// <summary>
/// Bounded single-consumer scheduler for EWrapper callbacks. The native reader never creates a
/// task per callback and the OMS observes one serialized event stream.
/// </summary>
public sealed class InteractiveBrokersSerializedEventScheduler :
    IAdapterEventScheduler,
    IDisposable,
    IAsyncDisposable
{
    /// <summary>Maximum number of native callbacks awaiting serialized delivery.</summary>
    public const int CallbackCapacity = 4_096;

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

    /// <summary>Starts the one long-lived callback consumer.</summary>
    public InteractiveBrokersSerializedEventScheduler() => _consumer = Task.Run(ConsumeAsync);

    /// <summary>Last callback or queue-overflow fault observed by the scheduler.</summary>
    public Exception? LastCallbackFault { get; private set; }

    /// <summary>Raised after a scheduled callback faults.</summary>
    public event Action<Exception>? CallbackFaulted;

    /// <inheritdoc />
    public void Schedule(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_callbacks.Writer.TryWrite(callback))
            return;
        if (_completed)
            throw new ObjectDisposedException(nameof(InteractiveBrokersSerializedEventScheduler));
        var fault = new InvalidOperationException("Interactive Brokers execution-event queue overflow.");
        LastCallbackFault = fault;
        CallbackFaulted?.Invoke(fault);
        throw fault;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _completed = true;
        _callbacks.Writer.TryComplete();
        if (!_insideConsumer.Value)
            _consumer.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
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
