using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Internal abstraction over a market-data + connection backend (IB, NinjaTrader, ...).
/// The repository owns this; nothing else should depend on it. Implementations are
/// responsible for marshalling raw broker callbacks onto a single producer thread —
/// the repository takes care of UI-thread dispatch above this seam.
///
/// Each implementation reads its own connection settings from injected options
/// (host/port/clientId for IB; account/dll-path for NinjaTrader), so <see cref="ConnectAsync"/>
/// takes only a cancellation token.
/// </summary>
public interface IBrokerClient : IAsyncDisposable
{
    BrokerKind Kind { get; }

    IObservable<ConnectionState> ConnectionState { get; }

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// The instruments this broker/account can trade, for populating instrument pickers.
    /// Brokers with a discovery API (Alpaca <c>ListAssets</c>, cTrader symbols list) return
    /// their live universe; brokers without one (IB, NinjaTrader) return a curated catalog
    /// (<see cref="CuratedInstrumentCatalog"/>). Safe to call once connected; callers fall
    /// back to a static catalog if the returned list is empty.
    /// </summary>
    Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract,
        BarSize barSize,
        TimeSpan duration,
        CancellationToken ct = default);

    IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract,
        BarSize barSize,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming tick-by-tick bid/ask quotes. The sequence completes when <paramref name="ct"/>
    /// is cancelled or the connection is permanently lost. Implementations are responsible
    /// for marshalling raw broker callbacks onto a single producer thread before yielding.
    /// </summary>
    IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming L2 order-book snapshots. Each emitted <see cref="DepthSnapshot"/> is a
    /// consistent view of the book at the time of the underlying broker event — the
    /// implementation is responsible for reconstructing snapshots from incremental
    /// (add/delete/replace) feeds before yielding.
    ///
    /// Only some brokers support L2: cTrader does (<c>ProtoOASubscribeDepthQuotesReq</c>);
    /// Interactive Brokers does (<c>reqMktDepth</c>, not yet wired); NinjaTrader's NTDirect
    /// surface does not. Implementations without depth support throw
    /// <see cref="NotSupportedException"/>; callers should be prepared to fall back to L1
    /// (<see cref="SubscribeTicksAsync"/>).
    /// </summary>
    IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract,
        int levels = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming trade prints (last-trade tape) as broker-shaped <see cref="TradeTick"/>s.
    /// The ingest layer wraps these into canonical <see cref="TradePrint"/>s. Brokers expose
    /// trades through different APIs (IB <c>reqTickByTickData("AllLast")</c>, Alpaca trade
    /// websocket channel, cTrader spot tick events); implementations without a trade feed
    /// throw <see cref="NotSupportedException"/> and ingest swallows it.
    ///
    /// When the broker reports the initiating side natively, set <see cref="TradeTick.Aggressor"/>;
    /// otherwise emit <see cref="AggressorSide.Unknown"/> and the ingest layer infers via the
    /// Lee-Ready quote rule against the current best bid/ask.
    /// </summary>
    IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract,
        CancellationToken ct = default);

    /// <summary>
    /// Order lifecycle events (acks, fills, cancels, rejects) for every order submitted
    /// through <see cref="PlaceOrderAsync"/>. Hot observable — multicast to all subscribers.
    /// Real broker clients that don't yet support OMS return <c>Observable.Empty</c>.
    /// </summary>
    IObservable<OrderEvent> OrderEvents { get; }

    /// <summary>
    /// Submits an order. The returned <see cref="OrderResult"/> reflects state at submission
    /// time only; subsequent transitions (fills, cancels) are pushed through
    /// <see cref="OrderEvents"/>. <see cref="OrderRequest.ClientOrderId"/> is an idempotency
    /// key: re-submitting with the same id MUST NOT produce a second order.
    /// </summary>
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Cancels a working order by its client-assigned id. Idempotent — cancelling an
    /// already-terminal order is a no-op.
    /// </summary>
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
}
