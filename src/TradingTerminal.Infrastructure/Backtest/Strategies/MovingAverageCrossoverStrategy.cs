using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Moving-average crossover. Go long when the fast SMA crosses above the slow SMA;
/// flip short on the inverse cross. The most famous trend-following baseline (Murphy,
/// "Technical Analysis of the Financial Markets", 1999). The 50/200 daily-bar variant is
/// the canonical "golden cross / death cross" headline used across financial media.
///
/// Tick analog: any pair of periods <see cref="FastPeriod"/> &lt; <see cref="SlowPeriod"/>.
/// Behavior is "always in the market" — flips between long and short on every cross.
/// </summary>
public sealed class MovingAverageCrossoverStrategy : IBacktestStrategy
{
    public int FastPeriod { get; }
    public int SlowPeriod { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.SimpleMovingAverage _fast;
    private readonly Indicators.SimpleMovingAverage _slow;
    private long _position;
    private int _orderSeq;
    private int _prevSign;

    public MovingAverageCrossoverStrategy(
        Contract contract,
        int fastPeriod = 50,
        int slowPeriod = 200,
        long quantity = 1)
    {
        if (fastPeriod >= slowPeriod) throw new ArgumentException("fast must be < slow");
        _contract = contract;
        FastPeriod = fastPeriod;
        SlowPeriod = slowPeriod;
        Quantity = quantity;
        _fast = new Indicators.SimpleMovingAverage(fastPeriod);
        _slow = new Indicators.SimpleMovingAverage(slowPeriod);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _fast.Push(mid);
        _slow.Push(mid);
        if (!_slow.IsReady) return;

        var sign = Math.Sign(_fast.Value - _slow.Value);
        if (sign == 0 || sign == _prevSign)
        {
            _prevSign = sign == 0 ? _prevSign : sign;
            return;
        }
        _prevSign = sign;

        // Flatten then open the opposite side. One trip = max 2 fills.
        if (_position != 0)
        {
            var flatSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, flatSide, Math.Abs(_position), ct);
            _position = 0;
        }
        if (sign > 0)
        {
            _position = Quantity;
            await Submit(router, OrderSide.Buy, Quantity, ct);
        }
        else
        {
            _position = -Quantity;
            await Submit(router, OrderSide.Sell, Quantity, ct);
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"ma-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
