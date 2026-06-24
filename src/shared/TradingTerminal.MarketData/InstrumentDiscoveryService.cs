using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Hosted background service that loads each connected broker's full tradable universe once
/// per connection and registers every contract into the canonical
/// <see cref="IInstrumentRegistry"/>. After this runs, every symbol the broker offers has a
/// stable <see cref="InstrumentId"/> + persisted alias before any strategy opens — so the
/// first per-strategy <c>ResolveOrCreate</c> is always a cache hit and historical-bar
/// lookups can key on <c>InstrumentId</c> without a synchronous broker round-trip.
///
/// Multi-broker: subscribes each available broker's per-broker state stream
/// (<see cref="IBrokerSelector.StateOf"/>) and runs an independent discovery pass on every
/// transition into <see cref="ConnectionState.Connected"/> for that broker. Discovery is
/// idempotent (the registry short-circuits on known aliases); a new Connected transition for
/// a broker cancels its previous in-flight pass so a quick reconnect doesn't double-register.
/// </summary>
internal sealed class InstrumentDiscoveryService : IHostedService, IDisposable
{
    private readonly IBrokerSelector _selector;
    private readonly IInstrumentRegistry _registry;
    private readonly ILogger<InstrumentDiscoveryService> _logger;

    private readonly List<IDisposable> _stateSubscriptions = new();
    private readonly ConcurrentDictionary<BrokerKind, CancellationTokenSource> _runs = new();

    public InstrumentDiscoveryService(
        IBrokerSelector selector,
        IInstrumentRegistry registry,
        ILogger<InstrumentDiscoveryService> logger)
    {
        _selector = selector;
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in _selector.AvailableKinds)
        {
            var broker = kind;
            var sub = _selector.StateOf(broker)
                .DistinctUntilChanged()
                .Where(state => state == ConnectionState.Connected)
                .Subscribe(_ => OnConnected(broker));
            _stateSubscriptions.Add(sub);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var sub in _stateSubscriptions) sub.Dispose();
        _stateSubscriptions.Clear();
        foreach (var cts in _runs.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* swallow */ }
        }
        _runs.Clear();
        return Task.CompletedTask;
    }

    private void OnConnected(BrokerKind broker)
    {
        var cts = new CancellationTokenSource();
        if (_runs.TryRemove(broker, out var prev))
        {
            try { prev.Cancel(); prev.Dispose(); } catch { /* swallow */ }
        }
        _runs[broker] = cts;
        _ = LoadUniverseAsync(broker, cts.Token);
    }

    private async Task LoadUniverseAsync(BrokerKind broker, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Instrument discovery starting for {Broker}", broker);
            var list = await _selector.Get(broker).ListInstrumentsAsync(ct).ConfigureAwait(false);
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
        foreach (var sub in _stateSubscriptions) sub.Dispose();
        _stateSubscriptions.Clear();
        foreach (var cts in _runs.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* swallow */ }
        }
        _runs.Clear();
    }
}
