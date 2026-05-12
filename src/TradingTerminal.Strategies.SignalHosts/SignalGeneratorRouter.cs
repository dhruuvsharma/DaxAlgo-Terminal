using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Strategies.SignalHosts;

/// <summary>
/// <see cref="IOrderRouter"/> for the live "signal generator" mode. Each <c>PlaceOrderAsync</c>
/// is treated as a signal — we capture the request, raise <see cref="SignalEmitted"/> with
/// metadata, and synthesise an immediate <c>Filled</c> <see cref="OrderEvent"/> so the
/// wrapped backtest strategy's internal position tracking stays correct.
///
/// Fill convention:
///   - Market / stop orders fill at the current touch on the relevant side (Buy → Ask,
///     Sell → Bid).
///   - Limit orders fill at the requested limit price (we don't track queue / lifetime in
///     signal mode — the goal is to surface what the strategy *wants*, not simulate fills).
///
/// The router needs to know the latest tick to set the fill price; the host view-model
/// calls <see cref="UpdateMarketContext"/> on every incoming live tick.
/// </summary>
public sealed class SignalGeneratorRouter : IOrderRouter
{
    private readonly Subject<OrderEvent> _events = new();
    private Tick? _lastTick;
    private long _nextBrokerSeq;

    public IObservable<OrderEvent> OrderEvents => _events.AsObservable();

    public event Action<SignalEntry>? SignalEmitted;

    /// <summary>Most recent live tick; used to price synthetic fills.</summary>
    public void UpdateMarketContext(Tick tick) => _lastTick = tick;

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var brokerId = $"SG-{Interlocked.Increment(ref _nextBrokerSeq)}";
        var (fillPrice, mid) = PriceFor(request);

        SignalEmitted?.Invoke(new SignalEntry(
            TimestampUtc: now,
            Side: request.Side,
            Quantity: request.Quantity,
            OrderType: request.Type,
            Price: fillPrice,
            Mid: mid));

        _events.OnNext(new OrderEvent(
            TimestampUtc: now,
            ClientOrderId: request.ClientOrderId,
            BrokerOrderId: brokerId,
            Side: request.Side,
            State: OrderState.Filled,
            FilledQuantity: request.Quantity,
            AverageFillPrice: fillPrice,
            LastFillQuantity: request.Quantity,
            LastFillPrice: fillPrice,
            Liquidity: request.Type == OrderType.Limit ? LiquidityFlag.Maker : LiquidityFlag.Taker));

        return Task.FromResult(new OrderResult(request.ClientOrderId, brokerId, OrderState.Filled));
    }

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        // Signal mode treats every submission as immediately filled, so there's nothing to
        // cancel. Emit a no-op cancel event so any strategy that listens for it sees a
        // terminal-ish transition rather than nothing.
        _events.OnNext(new OrderEvent(
            TimestampUtc: DateTime.UtcNow,
            ClientOrderId: clientOrderId,
            BrokerOrderId: null,
            Side: OrderSide.Buy,
            State: OrderState.Cancelled,
            FilledQuantity: 0,
            AverageFillPrice: null));
        return Task.CompletedTask;
    }

    private (double Price, double Mid) PriceFor(OrderRequest request)
    {
        if (_lastTick is null)
            return (request.LimitPrice ?? 0, 0);

        var t = _lastTick;
        var mid = (t.Bid + t.Ask) * 0.5;

        return request.Type switch
        {
            OrderType.Market => (request.Side == OrderSide.Buy ? t.Ask : t.Bid, mid),
            OrderType.Stop   => (request.Side == OrderSide.Buy ? t.Ask : t.Bid, mid),
            OrderType.Limit  => (request.LimitPrice ?? mid, mid),
            OrderType.StopLimit => (request.LimitPrice ?? mid, mid),
            _ => (mid, mid),
        };
    }
}
