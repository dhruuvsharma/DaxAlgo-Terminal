using System.Reactive.Linq;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Trading;

/// <summary>
/// Production <see cref="IOrderRouter"/>. Delegates to whatever <see cref="IBrokerClient"/>
/// is currently active via <see cref="IBrokerSelector"/>. The <see cref="OrderEvents"/>
/// stream switches with the active broker, so a strategy that survives a broker swap
/// keeps receiving events from the new broker without re-subscribing.
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
            .FromEventPattern(h => _selector.ActiveChanged += h, h => _selector.ActiveChanged -= h)
            .Select(_ => _selector.Active.OrderEvents)
            .StartWith(_selector.Active.OrderEvents)
            .Switch();

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        _selector.Active.PlaceOrderAsync(request, ct);

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
        _selector.Active.CancelOrderAsync(clientOrderId, ct);
}
