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
