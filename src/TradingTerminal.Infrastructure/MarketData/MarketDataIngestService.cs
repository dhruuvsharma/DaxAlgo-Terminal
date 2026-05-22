using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Default <see cref="IMarketDataIngest"/>. Subscribes the active broker's raw streams, normalizes
/// each event into a canonical record (assigning a per-instrument sequence and resolving the
/// timestamp semantics for that broker), then publishes to the hub and persists to the store.
/// Subscriptions are ref-counted per instrument+stream so multiple consumers share one broker feed.
///
/// Deliberately talks to <see cref="IBrokerClient"/> directly (not the UI-marshalling repository):
/// ingest runs entirely on background threads, and consumers get UI-thread delivery by choosing how
/// they observe the hub.
/// </summary>
internal sealed class MarketDataIngestService : IMarketDataIngest
{
    /// <summary>Brokers that stamp ticks with local arrival time rather than exchange time, so the
    /// canonical record's event time is flagged approximate. (Alpaca stamps real exchange time.)</summary>
    private static bool StampsArrivalTime(BrokerKind broker) => broker switch
    {
        BrokerKind.Alpaca => false,
        _ => true, // IB, NinjaTrader, cTrader currently use DateTime.UtcNow at construction
    };

    private sealed class Entry
    {
        public required CancellationTokenSource Cts { get; init; }
        public int RefCount;
        public long Sequence;
    }

    private readonly IBrokerSelector _selector;
    private readonly IInstrumentRegistry _registry;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataStore _store;
    private readonly ILogger<MarketDataIngestService> _logger;

    // Keyed by (InstrumentId, stream discriminator). Quotes+depth share key (id, "L1"); bars use (id, "B{size}").
    private readonly ConcurrentDictionary<(int, string), Entry> _entries = new();
    private readonly object _gate = new();

    public MarketDataIngestService(
        IBrokerSelector selector,
        IInstrumentRegistry registry,
        IMarketDataHub hub,
        IMarketDataStore store,
        ILogger<MarketDataIngestService> logger)
    {
        _selector = selector;
        _registry = registry;
        _hub = hub;
        _store = store;
        _logger = logger;
    }

    public InstrumentId Resolve(Contract contract) =>
        _registry.ResolveOrCreate(contract, _selector.ActiveKind);

    public IDisposable Subscribe(Contract contract)
    {
        var broker = _selector.ActiveKind;
        var id = _registry.ResolveOrCreate(contract, broker);
        var entry = Acquire((id.Value, "L1"), entry =>
        {
            _ = PumpQuotesAsync(contract, id, broker, entry, entry.Cts.Token);
            _ = PumpDepthAsync(contract, id, entry.Cts.Token);
        });
        return new Handle(this, (id.Value, "L1"), entry);
    }

    public IDisposable SubscribeBars(Contract contract, BarSize size)
    {
        var broker = _selector.ActiveKind;
        var id = _registry.ResolveOrCreate(contract, broker);
        var key = (id.Value, $"B{(int)size}");
        var entry = Acquire(key, entry =>
            _ = PumpBarsAsync(contract, id, size, broker, entry.Cts.Token));
        return new Handle(this, key, entry);
    }

    private Entry Acquire((int, string) key, Action<Entry> startPumps)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return existing;
            }
            var entry = new Entry { Cts = new CancellationTokenSource(), RefCount = 1 };
            _entries[key] = entry;
            startPumps(entry);
            return entry;
        }
    }

    private void Release((int, string) key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            if (--entry.RefCount > 0) return;
            _entries.TryRemove(key, out _);
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    private async Task PumpQuotesAsync(Contract contract, InstrumentId id, BrokerKind broker, Entry entry, CancellationToken ct)
    {
        var approx = StampsArrivalTime(broker);
        try
        {
            await foreach (var tick in _selector.Active.SubscribeTicksAsync(contract, ct).ConfigureAwait(false))
            {
                var seq = Interlocked.Increment(ref entry.Sequence);
                var quote = new Quote(
                    id, tick.TimestampUtc, DateTime.UtcNow,
                    tick.Bid, tick.Ask, tick.BidSize, tick.AskSize,
                    broker, seq, approx);
                _hub.PublishQuote(quote);
                _store.EnqueueQuote(quote);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Quote ingest ended for {Symbol}", contract.Symbol); }
    }

    private async Task PumpDepthAsync(Contract contract, InstrumentId id, CancellationToken ct)
    {
        try
        {
            await foreach (var snapshot in _selector.Active.SubscribeDepthAsync(contract, 10, ct).ConfigureAwait(false))
                _hub.PublishDepth(id, snapshot); // depth is live-only by design — not persisted
        }
        catch (OperationCanceledException) { }
        catch (NotSupportedException) { _logger.LogDebug("Depth ingest skipped for {Symbol}: broker has no L2", contract.Symbol); }
        catch (Exception ex) { _logger.LogDebug(ex, "Depth ingest ended for {Symbol}", contract.Symbol); }
    }

    private async Task PumpBarsAsync(Contract contract, InstrumentId id, BarSize size, BrokerKind broker, CancellationToken ct)
    {
        try
        {
            await foreach (var bar in _selector.Active.SubscribeBarsAsync(contract, size, ct).ConfigureAwait(false))
            {
                var canonical = OhlcvBar.FromBar(bar, id, size, broker, isFinal: true);
                _hub.PublishBar(canonical);
                _store.EnqueueBar(canonical);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Bar ingest ended for {Symbol}", contract.Symbol); }
    }

    private sealed class Handle : IDisposable
    {
        private readonly MarketDataIngestService _owner;
        private readonly (int, string) _key;
        private int _disposed;

        public Handle(MarketDataIngestService owner, (int, string) key, Entry _)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(_key);
        }
    }
}
