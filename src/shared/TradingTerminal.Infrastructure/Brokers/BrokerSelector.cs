using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Ib;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// Default <see cref="IBrokerSelector"/>. Holds one <see cref="IBrokerClient"/> + one
/// <see cref="ConnectionManager"/> per registered broker. Each broker has its own
/// independent connect lifecycle — connecting one doesn't touch the others.
/// </summary>
public sealed class BrokerSelector : IBrokerSelector, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<BrokerKind, IBrokerClient> _clients;
    private readonly IReadOnlyDictionary<BrokerKind, BrokerConnectionMode> _modes;
    private readonly IReadOnlyDictionary<BrokerKind, ConnectionManager> _managers;
    private readonly ILogger<BrokerSelector> _logger;
    private readonly object _gate = new();

    public BrokerSelector(
        IEnumerable<IBrokerClient> clients,
        IEnumerable<BrokerConnectionMode> modes,
        ILoggerFactory loggerFactory)
    {
        _clients = clients.ToDictionary(c => c.Kind);
        _modes = modes.ToDictionary(m => m.Broker);
        _logger = loggerFactory.CreateLogger<BrokerSelector>();

        if (_clients.Count == 0)
            throw new InvalidOperationException(
                "No broker clients registered. Build with at least one broker SDK present (TWS API for IB, NTDirect.dll for NinjaTrader, cTrader, Alpaca).");

        var managers = new Dictionary<BrokerKind, ConnectionManager>(_clients.Count);
        foreach (var (kind, client) in _clients)
        {
            var perBrokerLogger = loggerFactory.CreateLogger($"ConnectionManager[{kind}]");
            var mgr = new ConnectionManager(client, perBrokerLogger);
            mgr.ConnectionState.Subscribe(state =>
                StateChanged?.Invoke(this, new BrokerStateChangedEventArgs(kind, state)));
            managers[kind] = mgr;
        }
        _managers = managers;

        AvailableKinds = _clients.Keys.OrderBy(k => (int)k).ToArray();
    }

    public IReadOnlyList<BrokerKind> AvailableKinds { get; }

    public bool IsAvailable(BrokerKind kind) => _clients.ContainsKey(kind);

    public IReadOnlyList<BrokerKind> Connected
    {
        get
        {
            lock (_gate)
            {
                return _managers
                    .Where(kv => kv.Value.CurrentState == ConnectionState.Connected)
                    .Select(kv => kv.Key)
                    .OrderBy(k => (int)k)
                    .ToArray();
            }
        }
    }

    public bool IsConnected(BrokerKind kind) =>
        _managers.TryGetValue(kind, out var mgr) && mgr.CurrentState == ConnectionState.Connected;

    public IBrokerClient Get(BrokerKind kind)
    {
        if (!_clients.TryGetValue(kind, out var client))
            throw new InvalidOperationException(
                $"Broker {kind} is not available in this build. Install the corresponding SDK and rebuild.");
        return client;
    }

    public BrokerConnectionMode ModeOf(BrokerKind kind)
    {
        if (!_modes.TryGetValue(kind, out var mode))
            throw new InvalidOperationException($"No connection-mode metadata registered for {kind}.");
        return mode;
    }

    public IObservable<ConnectionState> StateOf(BrokerKind kind)
    {
        if (!_managers.TryGetValue(kind, out var mgr))
            throw new InvalidOperationException($"Broker {kind} is not available.");
        return mgr.ConnectionState;
    }

    public ConnectionState CurrentStateOf(BrokerKind kind)
    {
        if (!_managers.TryGetValue(kind, out var mgr))
            throw new InvalidOperationException($"Broker {kind} is not available.");
        return mgr.CurrentState;
    }

    public event EventHandler<BrokerStateChangedEventArgs>? StateChanged;

    public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default)
    {
        if (!_managers.TryGetValue(kind, out var mgr))
            throw new InvalidOperationException($"Broker {kind} is not available in this build.");
        _logger.LogInformation("Starting connect loop for {Broker}", kind);
        return mgr.StartAsync(ct);
    }

    public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default)
    {
        if (!_managers.TryGetValue(kind, out var mgr))
            throw new InvalidOperationException($"Broker {kind} is not available in this build.");
        _logger.LogInformation("Stopping connect loop for {Broker}", kind);
        return mgr.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var mgr in _managers.Values)
        {
            try { await mgr.DisposeAsync(); } catch { /* swallow */ }
        }
        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); } catch { /* swallow */ }
        }
    }
}
