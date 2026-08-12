using System.ComponentModel;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// App-lifetime projection of the currently constructable execution environment. Registered broker
/// adapters are observed directly, while transient execution clients contribute through bounded,
/// disposable publishers so a LIVE route cannot disappear from shell chrome with its tool window.
/// </summary>
public sealed class ExecutionModeStatusProjection : INotifyPropertyChanged, IDisposable
{
    public const int MaximumTrackedAdapters = 64;
    public const int MaximumPublishers = 16;

    private readonly object _gate = new();
    private readonly IBrokerExecutionAdapter[] _adapters;
    private readonly Dictionary<long, bool> _publishers = [];
    private bool _hasLiveExecution;
    private long _nextPublisherId;
    private int _disposed;

    public ExecutionModeStatusProjection(IEnumerable<IBrokerExecutionAdapter>? adapters = null)
    {
        _adapters = (adapters ?? [])
            .Take(MaximumTrackedAdapters + 1)
            .ToArray();
        if (_adapters.Length > MaximumTrackedAdapters)
        {
            throw new InvalidOperationException(
                $"No more than {MaximumTrackedAdapters} execution adapters may contribute to the global mode banner.");
        }

        var subscribed = 0;
        try
        {
            foreach (var adapter in _adapters)
            {
                adapter.EventReceived += OnAdapterEvent;
                subscribed++;
            }
        }
        catch
        {
            for (var index = 0; index < subscribed; index++)
            {
                try
                {
                    _adapters[index].EventReceived -= OnAdapterEvent;
                }
                catch
                {
                    // Preserve the original subscription failure after best-effort rollback.
                }
            }
            throw;
        }
        _hasLiveExecution = ComputeHasLiveExecution();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasLiveExecution
    {
        get
        {
            lock (_gate)
                return _hasLiveExecution;
        }
    }

    public string BannerLabel => HasLiveExecution
        ? "LIVE - REAL-MONEY EXECUTION ENABLED"
        : "PAPER - EXECUTION SAFE DEFAULT";

    /// <summary>Creates one bounded contribution owned by a transient execution client.</summary>
    public IExecutionModeStatusPublisher CreatePublisher()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_publishers.Count >= MaximumPublishers)
            {
                throw new InvalidOperationException(
                    $"No more than {MaximumPublishers} execution-mode publishers may be active.");
            }
            if (_nextPublisherId == long.MaxValue)
                throw new InvalidOperationException("The execution-mode publisher identity space is exhausted.");

            var id = ++_nextPublisherId;
            _publishers.Add(id, false);
            return new Publisher(this, id);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var adapter in _adapters)
        {
            try
            {
                adapter.EventReceived -= OnAdapterEvent;
            }
            catch
            {
                // App shutdown must continue detaching the remaining bounded adapter set.
            }
        }
        lock (_gate)
            _publishers.Clear();
        PropertyChanged = null;
    }

    private void OnAdapterEvent(BrokerAdapterEvent _) => Refresh();

    private void Publish(long id, bool hasLiveExecution)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_publishers.ContainsKey(id))
                return;
            _publishers[id] = hasLiveExecution;
        }
        Refresh();
    }

    private void RemovePublisher(long id)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_publishers.Remove(id))
                return;
        }
        Refresh();
    }

    private void Refresh()
    {
        bool changed;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var hasLiveExecution = ComputeHasLiveExecution();
            changed = hasLiveExecution != _hasLiveExecution;
            _hasLiveExecution = hasLiveExecution;
        }
        if (!changed)
            return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLiveExecution)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BannerLabel)));
    }

    private bool ComputeHasLiveExecution()
    {
        try
        {
            return _adapters.Any(adapter => adapter.Mode == ExecutionMode.Live) ||
                   _publishers.Values.Any(hasLiveExecution => hasLiveExecution);
        }
        catch
        {
            // A projection fault must make the operator-facing state louder, never falsely PAPER.
            return true;
        }
    }

    private sealed class Publisher(ExecutionModeStatusProjection owner, long id)
        : IExecutionModeStatusPublisher
    {
        private ExecutionModeStatusProjection? _owner = owner;

        public void Publish(bool hasLiveExecution) =>
            Volatile.Read(ref _owner)?.Publish(id, hasLiveExecution);

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemovePublisher(id);
    }
}

public interface IExecutionModeStatusPublisher : IDisposable
{
    void Publish(bool hasLiveExecution);
}
