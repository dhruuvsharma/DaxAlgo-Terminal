using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Hosted background service that loads the active broker's full tradable universe once
/// per connection and registers every contract into the canonical
/// <see cref="IInstrumentRegistry"/>. After this runs, every symbol the broker offers has a
/// stable <see cref="InstrumentId"/> + persisted alias before any strategy opens — so the
/// first per-strategy <c>ResolveOrCreate</c> is always a cache hit and historical-bar
/// lookups can key on <c>InstrumentId</c> without a synchronous broker round-trip.
///
/// Reacts to one trigger: an injected <see cref="IObservable{T}"/> of
/// <see cref="ConnectionState"/> transitions to <see cref="ConnectionState.Connected"/>.
/// (DI binds this to the ConnectionManager's state stream, which itself re-wires when the
/// user switches brokers — so a broker switch ⇒ Disconnected ⇒ Connected re-fires discovery
/// against the new broker automatically.)
///
/// Discovery is idempotent (the registry short-circuits on known aliases) and runs in the
/// background, so login never waits on it. A new Connected transition cancels any
/// in-flight discovery so a broker switch mid-load doesn't double-register.
/// </summary>
internal sealed class InstrumentDiscoveryService : IHostedService, IDisposable
{
    private readonly IBrokerSelector _selector;
    private readonly IObservable<ConnectionState> _connectionState;
    private readonly IInstrumentRegistry _registry;
    private readonly ILogger<InstrumentDiscoveryService> _logger;

    private IDisposable? _stateSubscription;
    private CancellationTokenSource? _runCts;
    private readonly object _gate = new();

    public InstrumentDiscoveryService(
        IBrokerSelector selector,
        IObservable<ConnectionState> connectionState,
        IInstrumentRegistry registry,
        ILogger<InstrumentDiscoveryService> logger)
    {
        _selector = selector;
        _connectionState = connectionState;
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stateSubscription = _connectionState
            .DistinctUntilChanged()
            .Where(state => state == ConnectionState.Connected)
            .Subscribe(_ => OnConnected());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stateSubscription?.Dispose();
        CancelInFlight();
        return Task.CompletedTask;
    }

    private void OnConnected()
    {
        CancelInFlight();
        var cts = new CancellationTokenSource();
        lock (_gate) _runCts = cts;
        _ = LoadUniverseAsync(_selector.ActiveKind, cts.Token);
    }

    private void CancelInFlight()
    {
        CancellationTokenSource? toCancel;
        lock (_gate)
        {
            toCancel = _runCts;
            _runCts = null;
        }
        if (toCancel is null) return;
        try { toCancel.Cancel(); } catch { /* already disposed */ }
        toCancel.Dispose();
    }

    private async Task LoadUniverseAsync(BrokerKind broker, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Instrument discovery starting for {Broker}", broker);
            var list = await _selector.Active.ListInstrumentsAsync(ct).ConfigureAwait(false);
            if (list is null || list.Count == 0)
            {
                _logger.LogInformation("Instrument discovery for {Broker}: broker returned no instruments", broker);
                return;
            }

            var registered = 0;
            foreach (var item in list)
            {
                if (ct.IsCancellationRequested) break;
                _registry.ResolveOrCreate(item.Contract, broker);
                registered++;
                // Yield occasionally so a 10k-symbol Alpaca pull doesn't pin a threadpool thread.
                if ((registered & 0x3FF) == 0)
                    await Task.Yield();
            }

            _logger.LogInformation(
                "Instrument discovery complete for {Broker}: {Registered}/{Total} contracts registered",
                broker, registered, list.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Instrument discovery cancelled for {Broker}", broker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instrument discovery failed for {Broker}", broker);
        }
    }

    public void Dispose()
    {
        _stateSubscription?.Dispose();
        CancelInFlight();
    }
}
