using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// Default <see cref="IBrokerSelector"/>. Holds one client per broker (resolved from DI
/// at startup) and a parallel <see cref="BrokerConnectionMode"/> map. The login screen
/// flips the active selection before the first connect.
/// </summary>
public sealed class BrokerSelector : IBrokerSelector
{
    private readonly IReadOnlyDictionary<BrokerKind, IBrokerClient> _clients;
    private readonly IReadOnlyDictionary<BrokerKind, BrokerConnectionMode> _modes;
    private readonly ILogger<BrokerSelector> _logger;
    private BrokerKind _active;

    public BrokerSelector(
        IEnumerable<IBrokerClient> clients,
        IEnumerable<BrokerConnectionMode> modes,
        ILogger<BrokerSelector> logger)
    {
        _clients = clients.ToDictionary(c => c.Kind);
        _modes = modes.ToDictionary(m => m.Broker);
        _logger = logger;
        // Default to IB so the existing tests / dev workflow keep working until the user picks otherwise.
        _active = _clients.ContainsKey(BrokerKind.InteractiveBrokers)
            ? BrokerKind.InteractiveBrokers
            : _clients.Keys.First();
    }

    public BrokerKind ActiveKind => _active;

    public IBrokerClient Active => _clients[_active];

    public BrokerConnectionMode ActiveMode => _modes[_active];

    public event EventHandler? ActiveChanged;

    public void SetActive(BrokerKind kind)
    {
        if (!_clients.ContainsKey(kind))
            throw new InvalidOperationException($"Broker {kind} is not registered.");
        if (_active == kind) return;
        _logger.LogInformation("Switching active broker {Old} -> {New}", _active, kind);
        _active = kind;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }
}
