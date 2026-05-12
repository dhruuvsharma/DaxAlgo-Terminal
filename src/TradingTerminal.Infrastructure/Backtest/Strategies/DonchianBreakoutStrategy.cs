using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Donchian channel breakout: when the ask crosses above the highest bid of the previous
/// <see cref="Lookback"/> ticks, go long. Symmetric on the short side. Exit on a trailing
/// stop a fixed price distance behind the most-favourable mid since entry. Not an edge —
/// a non-trivial trend-follower that complements the mean-reversion demo so the backtest
/// surface can show two opposite regime sensitivities at once.
/// </summary>
public sealed class DonchianBreakoutStrategy : IBacktestStrategy
{
    public int Lookback { get; }
    public double TrailingStopDistance { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Queue<double> _highs;
    private readonly Queue<double> _lows;
    private long _position;
    private double _peakSinceEntry;
    private double _troughSinceEntry;
    private int _orderSeq;

    public DonchianBreakoutStrategy(
        Contract contract,
        int lookback = 100,
        double trailingStopDistance = 0.15,
        long quantity = 1)
    {
        _contract = contract;
        Lookback = lookback;
        TrailingStopDistance = trailingStopDistance;
        Quantity = quantity;
        _highs = new Queue<double>(lookback);
        _lows = new Queue<double>(lookback);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;

        if (_position == 0 && _highs.Count == Lookback)
        {
            double channelHigh = double.MinValue, channelLow = double.MaxValue;
            foreach (var h in _highs) if (h > channelHigh) channelHigh = h;
            foreach (var l in _lows) if (l < channelLow) channelLow = l;

            if (tick.Ask > channelHigh)
            {
                _position = Quantity;
                _peakSinceEntry = mid;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (tick.Bid < channelLow)
            {
                _position = -Quantity;
                _troughSinceEntry = mid;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
        }
        else if (_position > 0)
        {
            if (mid > _peakSinceEntry) _peakSinceEntry = mid;
            if (tick.Bid <= _peakSinceEntry - TrailingStopDistance)
            {
                await Submit(router, OrderSide.Sell, _position, ct);
                _position = 0;
            }
        }
        else if (_position < 0)
        {
            if (mid < _troughSinceEntry) _troughSinceEntry = mid;
            if (tick.Ask >= _troughSinceEntry + TrailingStopDistance)
            {
                await Submit(router, OrderSide.Buy, -_position, ct);
                _position = 0;
            }
        }

        _highs.Enqueue(tick.Bid);
        _lows.Enqueue(tick.Ask);
        while (_highs.Count > Lookback) _highs.Dequeue();
        while (_lows.Count > Lookback) _lows.Dequeue();
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
        var id = $"don-{++_orderSeq}";
        return router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: id,
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
    }
}
