using System.Reactive.Linq;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Trading;

/// <summary>
/// Production <see cref="IOrderRouter"/>. Merges <see cref="IBrokerClient.OrderEvents"/>
/// from every connected broker into one stream; place/cancel calls pick the first connected
/// broker as a deliberate fallback.
///
/// NOTE — order routing is intentionally a fallback today: the UI uses brokers for signal
/// generation only, so we haven't wired per-order broker selection yet. When live trading
/// goes through this router, switch <see cref="PlaceOrderAsync"/> to resolve the target
/// broker from <c>OrderRequest.Contract</c>'s <c>InstrumentId.Source</c> (or an explicit
/// <c>BrokerKind</c> on the request). See the "Order routing follow-up" memory.
/// </summary>
public sealed class LiveOrderRouter : IOrderRouter
{
    private readonly IBrokerSelector _selector;

    public LiveOrderRouter(IBrokerSelector selector)
    {
        _selector = selector;
    }

    public IObservable<OrderEvent> OrderEvents =>
        Observable
            .FromEventPattern<BrokerStateChangedEventArgs>(
                h => _selector.StateChanged += h,
                h => _selector.StateChanged -= h)
            .StartWith((System.Reactive.EventPattern<BrokerStateChangedEventArgs>?)null!)
            .Select(_ => MergeConnectedOrderEvents())
            .Switch();

    private IObservable<OrderEvent> MergeConnectedOrderEvents()
    {
        var connected = _selector.Connected;
        if (connected.Count == 0) return Observable.Empty<OrderEvent>();
        return connected.Select(k => _selector.Get(k).OrderEvents).Merge();
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var broker = PickFallbackBroker();
        return _selector.Get(broker).PlaceOrderAsync(request, ct);
    }

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        var broker = PickFallbackBroker();
        return _selector.Get(broker).CancelOrderAsync(clientOrderId, ct);
    }

    private BrokerKind PickFallbackBroker()
    {
        var connected = _selector.Connected;
        if (connected.Count > 0) return connected[0];
        // No broker connected — surface a clear error rather than a NullReferenceException.
        throw new InvalidOperationException(
            "No broker is currently connected. Connect at least one broker in the login screen before placing orders.");
    }
}
