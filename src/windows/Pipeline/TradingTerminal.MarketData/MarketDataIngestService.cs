using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Default <see cref="IMarketDataIngest"/>. Subscribes a named broker's raw streams, normalizes
/// each event into a canonical record (assigning a per-instrument sequence and resolving the
/// timestamp semantics for that broker), then publishes to the hub. Subscriptions are
/// ref-counted per (instrument, broker, stream) so multiple consumers share one broker feed.
///
/// <para>Persistence is <b>tick-primary</b>: quotes and trades are written to the store; live bars
/// are not. Depth (L2) is written too, and lands on the backends that persist it — the default
/// per-broker SQLite store (its <c>…-l2.db</c> stream file) and QuestDB; the single-file SQLite and
/// Postgres backends no-op on depth. Bars at any cadence are derivable from
/// ticks; live-bar persistence was redundant storage. The bars table is still populated by the
/// historical-fetch caching path in
/// <see cref="MarketDataRepository"/> — that route covers time ranges before we connected, which
/// tick aggregation can't reach.</para>
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

    // Keyed by (InstrumentId, BrokerKind, stream discriminator). Quotes+depth share key (id, broker, "L1"); bars use (id, broker, "B{size}"); trades use (id, broker, "T").
    private readonly ConcurrentDictionary<(int, BrokerKind, string), Entry> _entries = new();
    private readonly object _gate = new();

    // Per-instrument context used by the Lee-Ready aggressor classifier when a broker's trade
    // tape doesn't report initiating side natively. Bid/ask are refreshed from the quote pump;
    // prior trade price + classification are kept from the trade pump for the tick-rule fallback.
    // Concurrent reads/writes of doubles are torn-tolerant in the worst case: a misclassified
    // print, never garbage.
    private sealed class TradeContext
    {
        public double Bid;
        public double Ask;
        public double PriorTradePrice;
        public AggressorSide PriorClassification = AggressorSide.Unknown;
    }
    private readonly ConcurrentDictionary<int, TradeContext> _tradeContexts = new();

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

    public InstrumentId Resolve(Contract contract, BrokerKind broker) =>
        _registry.ResolveOrCreate(contract, broker);

    public IDisposable Subscribe(Contract contract, BrokerKind broker)
    {
        var id = _registry.ResolveOrCreate(contract, broker);
        var key = (id.Value, broker, "L1");
        var entry = Acquire(key, entry =>
        {
            _ = PumpQuotesAsync(contract, id, broker, entry, entry.Cts.Token);
            _ = PumpDepthAsync(contract, id, broker, entry.Cts.Token);
        });
        return new Handle(this, key, entry);
    }

    public IDisposable SubscribeBars(Contract contract, BrokerKind broker, BarSize size)
    {
        var id = _registry.ResolveOrCreate(contract, broker);
        var key = (id.Value, broker, $"B{(int)size}");
        var entry = Acquire(key, entry =>
            _ = PumpBarsAsync(contract, id, size, broker, entry.Cts.Token));
        return new Handle(this, key, entry);
    }

    public IDisposable SubscribeTrades(Contract contract, BrokerKind broker)
    {
        var id = _registry.ResolveOrCreate(contract, broker);
        var key = (id.Value, broker, "T");
        var entry = Acquire(key, entry =>
            _ = PumpTradesAsync(contract, id, broker, entry, entry.Cts.Token));
        return new Handle(this, key, entry);
    }

    private Entry Acquire((int, BrokerKind, string) key, Action<Entry> startPumps)
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

    private void Release((int, BrokerKind, string) key)
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
            await foreach (var tick in _selector.Get(broker).SubscribeTicksAsync(contract, ct).ConfigureAwait(false))
            {
                var seq = Interlocked.Increment(ref entry.Sequence);
                var quote = new Quote(
                    id, tick.TimestampUtc, DateTime.UtcNow,
                    tick.Bid, tick.Ask, tick.BidSize, tick.AskSize,
                    broker, seq, approx);
                _hub.PublishQuote(quote);
                _store.EnqueueQuote(quote);

                // Keep the Lee-Ready aggressor classifier's view of best bid/ask fresh for any
                // concurrent trade pump on this instrument. Concurrent trade pumps without a
                // matching quote subscription fall back to the tick rule.
                var ctx = _tradeContexts.GetOrAdd(id.Value, _ => new TradeContext());
                ctx.Bid = tick.Bid;
                ctx.Ask = tick.Ask;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Quote ingest ended for {Symbol} on {Broker}", contract.Symbol, broker); }
    }

    private async Task PumpTradesAsync(Contract contract, InstrumentId id, BrokerKind broker, Entry entry, CancellationToken ct)
    {
        var approx = StampsArrivalTime(broker);
        try
        {
            await foreach (var trade in _selector.Get(broker).SubscribeTradesAsync(contract, ct).ConfigureAwait(false))
            {
                var ctx = _tradeContexts.GetOrAdd(id.Value, _ => new TradeContext());
                var aggressor = trade.Aggressor != AggressorSide.Unknown
                    ? trade.Aggressor
                    : Microstructure.ClassifyAggressor(trade.Price, ctx.Bid, ctx.Ask, ctx.PriorTradePrice, ctx.PriorClassification);
                ctx.PriorTradePrice = trade.Price;
                if (aggressor != AggressorSide.Unknown) ctx.PriorClassification = aggressor;

                var seq = Interlocked.Increment(ref entry.Sequence);
                var canonical = new TradePrint(
                    id, trade.TimestampUtc, DateTime.UtcNow,
                    trade.Price, trade.Size, aggressor,
                    broker, seq, approx);
                _hub.PublishTrade(canonical);
                _store.EnqueueTrade(canonical);
            }
        }
        catch (OperationCanceledException) { }
        catch (NotSupportedException) { _logger.LogDebug("Trade ingest skipped for {Symbol} on {Broker}: broker has no trade tape", contract.Symbol, broker); }
        catch (Exception ex) { _logger.LogWarning(ex, "Trade ingest ended for {Symbol} on {Broker}", contract.Symbol, broker); }
    }

    private async Task PumpDepthAsync(Contract contract, InstrumentId id, BrokerKind broker, CancellationToken ct)
    {
        try
        {
            await foreach (var snapshot in _selector.Get(broker).SubscribeDepthAsync(contract, 10, ct).ConfigureAwait(false))
            {
                _hub.PublishDepth(id, snapshot);
                // Depth (L2) is persisted by the backends built for its volume: the default per-broker
                // SQLite store (…-l2.db) and QuestDB. The single-file SQLite and Postgres stores no-op
                // on EnqueueDepth, so this stays a hub-only fan-out there.
                _store.EnqueueDepth(id, snapshot, broker);
            }
        }
        catch (OperationCanceledException) { }
        catch (NotSupportedException) { _logger.LogDebug("Depth ingest skipped for {Symbol} on {Broker}: broker has no L2", contract.Symbol, broker); }
        catch (Exception ex) { _logger.LogDebug(ex, "Depth ingest ended for {Symbol} on {Broker}", contract.Symbol, broker); }
    }

    private async Task PumpBarsAsync(Contract contract, InstrumentId id, BarSize size, BrokerKind broker, CancellationToken ct)
    {
        try
        {
            // Live bars fan out to the hub only — not persisted. Strategies that need bars on a
            // historical range that ticks don't cover go through MarketDataRepository.GetHistoricalBarsAsync,
            // which caches the broker's response via _store.EnqueueBar. See the class summary.
            await foreach (var bar in _selector.Get(broker).SubscribeBarsAsync(contract, size, ct).ConfigureAwait(false))
            {
                var canonical = OhlcvBar.FromBar(bar, id, size, broker, isFinal: true);
                _hub.PublishBar(canonical);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Bar ingest ended for {Symbol} on {Broker}", contract.Symbol, broker); }
    }

    private sealed class Handle : IDisposable
    {
        private readonly MarketDataIngestService _owner;
        private readonly (int, BrokerKind, string) _key;
        private int _disposed;

        public Handle(MarketDataIngestService owner, (int, BrokerKind, string) key, Entry _)
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
