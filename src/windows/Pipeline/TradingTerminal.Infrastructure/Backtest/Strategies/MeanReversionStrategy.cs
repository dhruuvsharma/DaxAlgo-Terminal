using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Simple mean-reversion demo: track the rolling mean of the mid over the last
/// <see cref="LookbackTicks"/> ticks. If price drops <see cref="EntryThreshold"/> below
/// the mean, market-buy. Exit market when price recovers to the mean (or stop out at
/// <see cref="StopThreshold"/> below entry). Symmetric on the short side.
///
/// Not a real edge — it's here as a non-trivial example for the backtest tab so the
/// equity curve and trades grid actually show something.
/// </summary>
public sealed class MeanReversionStrategy : IBacktestStrategy
{
    public int LookbackTicks { get; }
    public double EntryThreshold { get; }
    public double StopThreshold { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Queue<double> _window;
    private double _sum;
    private long _position;
    private double _entryPrice;
    private int _orderSeq;

    public MeanReversionStrategy(
        Contract contract,
        int lookbackTicks = 200,
        double entryThreshold = 0.10,
        double stopThreshold = 0.25,
        long quantity = 1)
    {
        _contract = contract;
        LookbackTicks = lookbackTicks;
        EntryThreshold = entryThreshold;
        StopThreshold = stopThreshold;
        Quantity = quantity;
        _window = new Queue<double>(lookbackTicks);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _window.Enqueue(mid);
        _sum += mid;
        while (_window.Count > LookbackTicks) _sum -= _window.Dequeue();
        if (_window.Count < LookbackTicks) return;

        var mean = _sum / _window.Count;
        var distance = mid - mean;

        if (_position == 0)
        {
            if (distance <= -EntryThreshold)
            {
                _entryPrice = tick.Ask;
                _position = Quantity;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (distance >= EntryThreshold)
            {
                _entryPrice = tick.Bid;
                _position = -Quantity;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        if (_position > 0)
        {
            var hitTarget = mid >= mean;
            var hitStop = tick.Bid <= _entryPrice - StopThreshold;
            if (hitTarget || hitStop)
            {
                await Submit(router, OrderSide.Sell, _position, ct);
                _position = 0;
            }
        }
        else
        {
            var hitTarget = mid <= mean;
            var hitStop = tick.Ask >= _entryPrice + StopThreshold;
            if (hitTarget || hitStop)
            {
                await Submit(router, OrderSide.Buy, -_position, ct);
                _position = 0;
            }
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct)
    {
        var id = $"mr-{++_orderSeq}";
        return router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: id,
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
    }
}
