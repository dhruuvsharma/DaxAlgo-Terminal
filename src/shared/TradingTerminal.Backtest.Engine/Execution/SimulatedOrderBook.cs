using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// Holds working orders across every instrument and evaluates fills on each quote via an injected
/// <see cref="IFillModel"/>. Fully synchronous — driven by the engine's single-threaded replay loop.
/// Raises <see cref="Event"/> for each order transition, tagged with the instrument so the engine can
/// route fills to the right position. Ported from the legacy single-instrument
/// <c>SimulatedOrderBook</c>, keyed by instrument.
/// </summary>
internal sealed class SimulatedOrderBook
{
    private readonly SimClock _clock;
    private readonly IFillModel _fillModel;
    private readonly Func<InstrumentId, double> _tickSizeOf;
    private readonly Dictionary<string, WorkingOrder> _byClientId = new(StringComparer.Ordinal);
    private long _nextBrokerId;

    public SimulatedOrderBook(SimClock clock, IFillModel fillModel, Func<InstrumentId, double> tickSizeOf)
    {
        _clock = clock;
        _fillModel = fillModel;
        _tickSizeOf = tickSizeOf;
    }

    /// <summary>Raised for every order transition: the instrument the order trades, then the event.</summary>
    public event Action<InstrumentId, OrderEvent>? Event;

    public OrderResult Submit(OrderRequest request, InstrumentId instrument)
    {
        if (_byClientId.TryGetValue(request.ClientOrderId, out var existing))
            return new OrderResult(request.ClientOrderId, existing.BrokerOrderId, existing.State);

        var brokerId = $"BT-{Interlocked.Increment(ref _nextBrokerId)}";
        var order = new WorkingOrder { Request = request, Instrument = instrument, BrokerOrderId = brokerId };
        _byClientId.Add(request.ClientOrderId, order);

        Event?.Invoke(instrument, new OrderEvent(
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

        Event?.Invoke(order.Instrument, new OrderEvent(
            _clock.UtcNow, clientOrderId, order.BrokerOrderId, order.Request.Side, OrderState.Cancelled,
            order.FilledQuantity, order.AveragePrice));
    }

    /// <summary>Evaluate fills for the orders resting on one instrument against its latest quote.</summary>
    public void OnQuote(InstrumentId instrument, Tick tick)
    {
        if (_byClientId.Count == 0) return;
        var tickSize = _tickSizeOf(instrument);

        foreach (var order in _byClientId.Values.ToList())
        {
            if (order.Instrument != instrument || IsTerminal(order.State)) continue;
            if (!_fillModel.TryFill(order, tick, tickSize, out var price, out var qty)) continue;

            order.FilledQuantity += qty;
            order.TotalFillValue += price * qty;

            var newState = order.FilledQuantity >= order.Request.Quantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            order.State = newState;

            var liquidity = order.Request.Type == OrderType.Limit ? LiquidityFlag.Maker : LiquidityFlag.Taker;

            Event?.Invoke(instrument, new OrderEvent(
                tick.TimestampUtc, order.Request.ClientOrderId, order.BrokerOrderId,
                order.Request.Side, newState,
                order.FilledQuantity, order.AveragePrice,
                LastFillQuantity: qty, LastFillPrice: price, Liquidity: liquidity));

            if (newState == OrderState.Filled)
                _byClientId.Remove(order.Request.ClientOrderId);
        }
    }

    private static bool IsTerminal(OrderState s) =>
        s is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;
}
