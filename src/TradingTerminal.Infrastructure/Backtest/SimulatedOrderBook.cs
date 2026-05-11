using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Holds working orders and evaluates fills on every tick using an injected
/// <see cref="IFillModel"/>. Fully synchronous — driven by the engine's replay loop on a
/// single thread. Exposes <see cref="Events"/> for the order router (and indirectly the
/// strategy) to subscribe to.
/// </summary>
public sealed class SimulatedOrderBook
{
    private readonly IClock _clock;
    private readonly IFillModel _fillModel;
    private readonly Subject<OrderEvent> _events = new();
    private readonly Dictionary<string, PendingOrder> _byClientId = new(StringComparer.Ordinal);
    private long _nextBrokerId;

    public SimulatedOrderBook(IClock clock, IFillModel fillModel)
    {
        _clock = clock;
        _fillModel = fillModel;
    }

    public IObservable<OrderEvent> Events => _events.AsObservable();

    public OrderResult Submit(OrderRequest request)
    {
        if (_byClientId.ContainsKey(request.ClientOrderId))
        {
            var existing = _byClientId[request.ClientOrderId];
            return new OrderResult(request.ClientOrderId, existing.BrokerOrderId, existing.State);
        }

        var brokerId = $"BT-{Interlocked.Increment(ref _nextBrokerId)}";
        var pending = new PendingOrder { Request = request, BrokerOrderId = brokerId };
        _byClientId.Add(request.ClientOrderId, pending);

        _events.OnNext(new OrderEvent(
            _clock.UtcNow, request.ClientOrderId, brokerId, request.Side, OrderState.Working,
            FilledQuantity: 0, AverageFillPrice: null));

        return new OrderResult(request.ClientOrderId, brokerId, OrderState.Working);
    }

    public void Cancel(string clientOrderId)
    {
        if (!_byClientId.TryGetValue(clientOrderId, out var order)) return;
        if (IsTerminal(order.State)) return;

        order.State = OrderState.Cancelled;
        _byClientId.Remove(clientOrderId);

        _events.OnNext(new OrderEvent(
            _clock.UtcNow, clientOrderId, order.BrokerOrderId, order.Request.Side, OrderState.Cancelled,
            order.FilledQuantity, order.AveragePrice));
    }

    public void OnTick(Tick tick)
    {
        if (_byClientId.Count == 0) return;

        // Snapshot to allow removals while iterating.
        var orders = _byClientId.Values.ToList();
        foreach (var order in orders)
        {
            if (IsTerminal(order.State)) continue;
            if (!_fillModel.TryFill(order, tick, out var price, out var qty)) continue;

            order.FilledQuantity += qty;
            order.TotalFillValue += price * qty;

            var newState = order.FilledQuantity >= order.Request.Quantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            order.State = newState;

            _events.OnNext(new OrderEvent(
                tick.TimestampUtc, order.Request.ClientOrderId, order.BrokerOrderId,
                order.Request.Side, newState,
                order.FilledQuantity, order.AveragePrice,
                LastFillQuantity: qty, LastFillPrice: price));

            if (newState == OrderState.Filled)
                _byClientId.Remove(order.Request.ClientOrderId);
        }
    }

    private static bool IsTerminal(OrderState s) =>
        s is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;
}
