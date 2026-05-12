using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// MACD signal-line crossover. MACD = EMA(<see cref="FastPeriod"/>) - EMA(<see cref="SlowPeriod"/>);
/// signal = EMA(<see cref="SignalPeriod"/>) of MACD. Long when MACD crosses above signal,
/// short when below. The textbook MACD trade documented in Appel's "Technical Analysis:
/// Power Tools for Active Investors" (2005) and a staple of every FX charting platform.
///
/// Default 12/26/9 periods are Appel's original recommendation; tick-level execution makes
/// the cross fire much faster than the daily-bar convention.
/// </summary>
public sealed class MacdCrossoverStrategy : IBacktestStrategy
{
    public int FastPeriod { get; }
    public int SlowPeriod { get; }
    public int SignalPeriod { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.ExponentialMovingAverage _fast;
    private readonly Indicators.ExponentialMovingAverage _slow;
    private readonly Indicators.ExponentialMovingAverage _signal;
    private long _position;
    private int _orderSeq;
    private int _prevSign;

    public MacdCrossoverStrategy(
        Contract contract,
        int fastPeriod = 12,
        int slowPeriod = 26,
        int signalPeriod = 9,
        long quantity = 1)
    {
        if (fastPeriod >= slowPeriod) throw new ArgumentException("fast must be < slow");
        _contract = contract;
        FastPeriod = fastPeriod;
        SlowPeriod = slowPeriod;
        SignalPeriod = signalPeriod;
        Quantity = quantity;
        _fast = new Indicators.ExponentialMovingAverage(fastPeriod);
        _slow = new Indicators.ExponentialMovingAverage(slowPeriod);
        _signal = new Indicators.ExponentialMovingAverage(signalPeriod);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _fast.Push(mid);
        _slow.Push(mid);
        if (!_slow.IsReady) return;

        var macd = _fast.Value - _slow.Value;
        _signal.Push(macd);
        if (!_signal.IsReady) return;

        var sign = Math.Sign(macd - _signal.Value);
        if (sign == 0 || sign == _prevSign)
        {
            _prevSign = sign == 0 ? _prevSign : sign;
            return;
        }
        _prevSign = sign;

        if (_position != 0)
        {
            var flatSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, flatSide, Math.Abs(_position), ct);
            _position = 0;
        }
        if (sign > 0) { _position = Quantity; await Submit(router, OrderSide.Buy, Quantity, ct); }
        else { _position = -Quantity; await Submit(router, OrderSide.Sell, Quantity, ct); }
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
            ClientOrderId: $"macd-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
