using System.Reactive.Linq;
using System.Runtime.CompilerServices;
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
/// </summary>
public sealed class MarketDataRepository : IMarketDataRepository, IAsyncDisposable
{
    private readonly IBrokerSelector _selector;
    private readonly ConnectionManager _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MarketDataRepository> _logger;

    public MarketDataRepository(
        IBrokerSelector selector,
        ConnectionManager connection,
        IUiDispatcher dispatcher,
        ILogger<MarketDataRepository> logger)
    {
        _selector = selector;
        _connection = connection;
        _dispatcher = dispatcher;
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
        _logger.LogDebug("Historical {Symbol} {Size} duration={Duration}",
            contract.Symbol, barSize.ToDisplayString(), duration);
        return await _selector.Active.RequestHistoricalBarsAsync(contract, barSize, duration, ct);
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Caller may be on a worker thread (test) or on the UI thread (live app).
        // Either way, marshal each yielded bar onto the UI thread so view-models stay simple.
        await foreach (var bar in _selector.Active.SubscribeBarsAsync(contract, barSize, ct))
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

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var tick in _selector.Active.SubscribeTicksAsync(contract, ct))
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
