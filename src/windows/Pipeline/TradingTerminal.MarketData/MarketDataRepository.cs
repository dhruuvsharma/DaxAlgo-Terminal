using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Default <see cref="IMarketDataRepository"/>. Routes per-request to the broker named in the
/// call (no "active broker" — see <see cref="IBrokerSelector"/>). Marshals every emitted bar/
/// tick/depth onto the UI thread before yielding to consumers.
///
/// Live tick + bar reads route through the canonical pipeline
/// (<see cref="IMarketDataIngest"/> + <see cref="IMarketDataHub"/>): subscribing here
/// auto-starts the ref-counted broker pump for that (instrument, broker), normalizes each event
/// into a canonical <see cref="Quote"/>/<see cref="OhlcvBar"/>, fans out via the hub, and persists
/// to the store. Consumers still get plain <see cref="Tick"/>/<see cref="Bar"/> records so no
/// strategy code changes — the database fills as a side effect of live trading.
///
/// Historical-bar reads are cache-first: the local <see cref="IMarketDataStore"/> is consulted
/// before the broker, and a broker fetch is only issued when the cache is too small or too stale.
/// Broker fetches are persisted back to the store so the next call is a hit.
///
/// Depth stays on the direct broker path because the ingest layer silently absorbs the
/// "broker has no L2" <see cref="NotSupportedException"/>, which existing callers rely on to
/// degrade gracefully.
/// </summary>
public sealed class MarketDataRepository : IMarketDataRepository
{
    private readonly IBrokerSelector _selector;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMarketDataIngest _ingest;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataStore _store;
    private readonly ILogger<MarketDataRepository> _logger;

    public MarketDataRepository(
        IBrokerSelector selector,
        IUiDispatcher dispatcher,
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        IMarketDataStore store,
        ILogger<MarketDataRepository> logger)
    {
        _selector = selector;
        _dispatcher = dispatcher;
        _ingest = ingest;
        _hub = hub;
        _store = store;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        var connected = _selector.Connected;
        if (connected.Count == 0) return Array.Empty<TradableInstrument>();

        var merged = new List<TradableInstrument>();
        foreach (var kind in connected)
        {
            try
            {
                var list = await _selector.Get(kind).ListInstrumentsAsync(ct).ConfigureAwait(false);
                if (list is null) continue;
                // Re-stamp Broker defensively — broker clients may have constructed rows before
                // this field existed in their own code path. The selector knows the truth.
                foreach (var item in list)
                    merged.Add(item.Broker == kind ? item : item with { Broker = kind });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ListInstrumentsAsync failed for {Broker}; skipping", kind);
            }
        }
        return merged;
    }

    public async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Contract contract, BrokerKind broker, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        // Cache-first: consult the local store before hitting the broker. The cache is
        // considered fresh enough if it holds at least the requested count AND the newest
        // bar is within two bar-widths of "now" (so a strategy reopened a few seconds later
        // doesn't re-pay the historical round-trip). On miss or stale, fetch from the
        // broker, persist every bar (UPSERT-safe), and return the fresh result.
        //
        // The store's identity is canonical (broker-neutral), so a single instrument's bars
        // are shared across brokers. That's correct for the data, but it must NOT let one
        // broker's cached bars satisfy a request that explicitly named a different broker —
        // otherwise whichever broker first populated the symbol would silently "win" every
        // later fetch and the caller's broker choice would be ignored. We therefore only treat
        // the cache as a hit when its newest bar was sourced from the requested broker.
        var instrumentId = _ingest.Resolve(contract, broker);
        var barSpan = barSize.ToTimeSpan();
        var requiredCount = Math.Max(1, (int)(duration.Ticks / Math.Max(1, barSpan.Ticks)));
        var freshnessWindow = TimeSpan.FromTicks(barSpan.Ticks * 2);

        var cached = await _store.GetRecentBarsAsync(instrumentId, barSize, requiredCount, broker, ct);
        if (cached.Count >= requiredCount &&
            DateTime.UtcNow - cached[^1].OpenTimeUtc <= freshnessWindow &&
            cached[^1].Source == broker)
        {
            _logger.LogDebug("Historical {Symbol} {Size} cache-hit ({Count} bars, source {Broker})",
                contract.Symbol, barSize.ToDisplayString(), cached.Count, broker);
            return cached.Select(b => b.ToBar()).ToArray();
        }

        _logger.LogDebug("Historical {Symbol} {Size} duration={Duration} cache-miss → broker {Broker} (cached={Cached})",
            contract.Symbol, barSize.ToDisplayString(), duration, broker, cached.Count);
        var fresh = await _selector.Get(broker).RequestHistoricalBarsAsync(contract, barSize, duration, ct);
        foreach (var bar in fresh)
            _store.EnqueueBar(OhlcvBar.FromBar(bar, instrumentId, barSize, broker, isFinal: true));
        return fresh;
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BrokerKind broker, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var instrumentId = _ingest.Resolve(contract, broker);
        var dropMeter = new FeedDropMeter();
        var channel = FeedChannel.CreateDropOldest<Bar>(FeedChannel.Capacity.Bars, onItemDropped: _ =>
        {
            if (dropMeter.Record())
                _logger.LogWarning(
                    "Live bar bridge for {Symbol} ({Broker}) shed its oldest queued bars ({Dropped} total) — consumer is not keeping up",
                    contract.Symbol, broker, dropMeter.Dropped);
        });

        using var subscription = _hub.Bars(instrumentId, barSize).Subscribe(canonical =>
            channel.Writer.TryWrite(canonical.ToBar()));
        using var handle = _ingest.SubscribeBars(contract, broker, barSize);

        try
        {
            await foreach (var bar in channel.Reader.ReadAllAsync(ct))
            {
                if (_dispatcher.CheckAccess())
                {
                    yield return bar;
                }
                else
                {
                    Bar marshalled = bar;
                    await _dispatcher.InvokeAsync(() => { /* ensure we're on UI before yielding */ });
                    yield return marshalled;
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, BrokerKind broker,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var instrumentId = _ingest.Resolve(contract, broker);
        var dropMeter = new FeedDropMeter();
        var channel = FeedChannel.CreateDropOldest<Tick>(FeedChannel.Capacity.Quotes, onItemDropped: _ =>
        {
            if (dropMeter.Record())
                _logger.LogWarning(
                    "Live tick bridge for {Symbol} ({Broker}) shed its oldest queued ticks ({Dropped} total) — consumer is not keeping up",
                    contract.Symbol, broker, dropMeter.Dropped);
        });

        using var subscription = _hub.Quotes(instrumentId).Subscribe(quote =>
            channel.Writer.TryWrite(new Tick(
                quote.EventTimeUtc, quote.Bid, quote.Ask, quote.BidSize, quote.AskSize)));
        using var handle = _ingest.Subscribe(contract, broker);

        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(ct))
            {
                if (_dispatcher.CheckAccess())
                {
                    yield return tick;
                }
                else
                {
                    Tick marshalled = tick;
                    await _dispatcher.InvokeAsync(() => { /* ensure we're on UI before yielding */ });
                    yield return marshalled;
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, BrokerKind broker,
        int levels = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var snapshot in _selector.Get(broker).SubscribeDepthAsync(contract, levels, ct))
        {
            if (_dispatcher.CheckAccess())
            {
                yield return snapshot;
            }
            else
            {
                DepthSnapshot marshalled = snapshot;
                await _dispatcher.InvokeAsync(() => { /* ensure we're on UI before yielding */ });
                yield return marshalled;
            }
        }
    }
}
