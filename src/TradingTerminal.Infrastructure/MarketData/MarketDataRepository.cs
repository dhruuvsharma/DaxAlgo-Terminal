using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Default <see cref="IMarketDataRepository"/>. Owns the <see cref="ConnectionManager"/>
/// (so the rest of the app can call <see cref="ConnectAsync"/> idempotently) and marshals
/// every emitted bar onto the UI thread before yielding to consumers.
///
/// Broker-neutral: the underlying client is resolved through <see cref="IBrokerSelector"/>
/// so the same repository instance serves whichever broker the user picked at login.
///
/// Live tick + bar reads route through the canonical pipeline
/// (<see cref="IMarketDataIngest"/> + <see cref="IMarketDataHub"/>): subscribing here
/// auto-starts the ref-counted broker pump, normalizes each event into a canonical
/// <see cref="Quote"/>/<see cref="OhlcvBar"/>, fans out via the hub, and persists to the
/// store. Consumers still get plain <see cref="Tick"/>/<see cref="Bar"/> records so no
/// strategy code changes — the database fills as a side effect of live trading.
/// Historical-bar reads (<see cref="GetHistoricalBarsAsync"/>) are cache-first: the local
/// <see cref="IMarketDataStore"/> is consulted before the broker, and a broker fetch is
/// only issued when the cache is too small or too stale. Broker fetches are persisted
/// back to the store so the next call is a hit.
/// Depth stays on the direct broker path because the ingest layer silently absorbs the
/// "broker has no L2" <see cref="NotSupportedException"/>, which existing callers rely on
/// to degrade gracefully.
/// </summary>
public sealed class MarketDataRepository : IMarketDataRepository, IAsyncDisposable
{
    private readonly IBrokerSelector _selector;
    private readonly ConnectionManager _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMarketDataIngest _ingest;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataStore _store;
    private readonly ILogger<MarketDataRepository> _logger;

    public MarketDataRepository(
        IBrokerSelector selector,
        ConnectionManager connection,
        IUiDispatcher dispatcher,
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        IMarketDataStore store,
        ILogger<MarketDataRepository> logger)
    {
        _selector = selector;
        _connection = connection;
        _dispatcher = dispatcher;
        _ingest = ingest;
        _hub = hub;
        _store = store;
        _logger = logger;
    }

    public IObservable<ConnectionState> ConnectionState => _connection.ConnectionState;

    public Task ConnectAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    public Task DisconnectAsync(CancellationToken ct = default) => _connection.StopAsync(ct);

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default) =>
        _selector.Active.ListInstrumentsAsync(ct);

    public async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        // Cache-first: consult the local store before hitting the broker. The cache is
        // considered fresh enough if it holds at least the requested count AND the newest
        // bar is within two bar-widths of "now" (so a strategy reopened a few seconds later
        // doesn't re-pay the historical round-trip). On miss or stale, fetch from the
        // broker, persist every bar (UPSERT-safe), and return the fresh result.
        var instrumentId = _ingest.Resolve(contract);
        var barSpan = barSize.ToTimeSpan();
        var requiredCount = Math.Max(1, (int)(duration.Ticks / Math.Max(1, barSpan.Ticks)));
        var freshnessWindow = TimeSpan.FromTicks(barSpan.Ticks * 2);

        var cached = await _store.GetRecentBarsAsync(instrumentId, barSize, requiredCount, ct);
        if (cached.Count >= requiredCount &&
            DateTime.UtcNow - cached[^1].OpenTimeUtc <= freshnessWindow)
        {
            _logger.LogDebug("Historical {Symbol} {Size} cache-hit ({Count} bars)",
                contract.Symbol, barSize.ToDisplayString(), cached.Count);
            return cached.Select(b => b.ToBar()).ToArray();
        }

        _logger.LogDebug("Historical {Symbol} {Size} duration={Duration} cache-miss → broker (cached={Cached})",
            contract.Symbol, barSize.ToDisplayString(), duration, cached.Count);
        var fresh = await _selector.Active.RequestHistoricalBarsAsync(contract, barSize, duration, ct);
        var broker = _selector.ActiveKind;
        foreach (var bar in fresh)
            _store.EnqueueBar(OhlcvBar.FromBar(bar, instrumentId, barSize, broker, isFinal: true));
        return fresh;
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Pipeline path: resolve canonical id, subscribe the hub first (so we don't miss the
        // first publish), then turn on the ref-counted broker pump via the ingest service.
        // Each emitted canonical OhlcvBar lands in the store AND fans out here — we project
        // back to the legacy Bar shape for the caller and UI-marshal as before.
        var instrumentId = _ingest.Resolve(contract);
        var channel = Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var subscription = _hub.Bars(instrumentId, barSize).Subscribe(canonical =>
            channel.Writer.TryWrite(canonical.ToBar()));
        using var handle = _ingest.SubscribeBars(contract, barSize);

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
        Contract contract,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Pipeline path: resolve canonical id, subscribe to hub.Quotes(id) first, then start
        // (or join) the ref-counted ingest pump. The ingest service writes every quote to the
        // store as a side effect; we project Quote → Tick here so legacy consumers (every
        // current strategy) keep their existing shape.
        var instrumentId = _ingest.Resolve(contract);
        var channel = Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var subscription = _hub.Quotes(instrumentId).Subscribe(quote =>
            channel.Writer.TryWrite(new Tick(
                quote.EventTimeUtc, quote.Bid, quote.Ask, quote.BidSize, quote.AskSize)));
        using var handle = _ingest.Subscribe(contract);

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
        Contract contract,
        int levels = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var snapshot in _selector.Active.SubscribeDepthAsync(contract, levels, ct))
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

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _selector.Active.DisposeAsync();
    }
}
